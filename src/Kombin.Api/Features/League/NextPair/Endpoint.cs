using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Features.League.NextPair;

/// <summary>One side of a pair: the outfit's pieces as slot and image, nothing else (no entry ID, no owner, no occasion or audience label).</summary>
internal sealed record LeagueSideResponse(IReadOnlyList<LeaguePieceResponse> Pieces);

/// <summary><c>Id</c>: random per serve, the only identifier the voter sees. <c>Sequence</c>: 1 to the day's limit (a number is never reused,
/// even when the pair it named was deleted with an account).</summary>
internal sealed record LeaguePairResponse(Guid Id, LeagueSideResponse Left, LeagueSideResponse Right, int Sequence);

/// <summary>Why there is no pair: the day's limit is reached ("bugünlük bitti"), or no outfit is left to show this voter.</summary>
internal enum LeagueNoPairReason
{
    DailyLimit,
    NoPairs,
}

/// <summary>
/// The next pair to vote on, or none with a reason. <c>CountedToday</c> / <c>ServedToday</c>: the caller's counted votes and served pairs today
/// (served = the last sequence, so it agrees with the limit);
/// <c>Target</c>: counted votes for the voting points; <c>Limit</c>: pairs a day; <c>ClosesAt</c>: when votes stop.
/// </summary>
internal sealed record NextPairResponse(
    LeaguePairResponse? Pair, LeagueNoPairReason? Reason, int CountedToday, int ServedToday, int Target, int Limit, DateTimeOffset ClosesAt);

internal static class NextPairEndpoint
{
    /// <summary>A pair lost to a concurrent request (a double tap, an entry withdrawn in between) is chosen again at most this many times.</summary>
    public const int Attempts = 3;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/pairs/next", Handle)
            .WithName("GetNextLeaguePair")
            .Produces<NextPairResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Serves the caller's next pair (design note section 6). An unanswered pair is returned again (asking again never re-rolls, so no one can
    /// fish for an outfit); after <c>League:MaxPairsPerDay</c> served pairs the answer is <c>DailyLimit</c> (skips and too-fast votes count; the
    /// count is the last sequence, so pairs deleted with another player's account still count and their numbers are never reused);
    /// otherwise <see cref="Pairing.Choose"/> picks from the least exposed candidates and the pair is stored with both entries' exposure
    /// counters (and their lock: an entry no longer follows its owner's wears once shown) in one transaction, the two entry rows locked in ID
    /// order like the vote does, so concurrent serves and votes on the same two outfits never deadlock. Errors: 403
    /// <c>League.NotJoined</c>; 409 <c>League.DayClosed</c> from the day's close on.
    /// </summary>
    private static async Task<IResult> Handle(
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        IOptions<LeagueOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var voterId = currentUser.UserId;
        if (!await db.LeaguePlayers.AnyAsync(player => player.UserId == voterId && player.LeftAt == null, cancellationToken))
        {
            return LeagueErrors.NotJoined.ToHttpResult();
        }

        var now = clock.UtcNow;
        var day = Istanbul.Today(now);
        var closesAt = LeagueDays.ClosesAt(day, settings.DayClosesAt);
        if (now >= closesAt || await db.LeagueDays.AnyAsync(row => row.Day == day && row.ClosedAt != null, cancellationToken))
        {
            return LeagueErrors.DayClosed.ToHttpResult();
        }

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var open = await db.LeaguePairs.AsNoTracking().FirstOrDefaultAsync(pair => pair.VoterId == voterId && pair.Day == day && pair.AnsweredAt == null, cancellationToken);
            if (open is not null)
            {
                return await RespondAsync(open, null);
            }

            var served = await db.LeaguePairs.AsNoTracking()
                .Where(pair => pair.VoterId == voterId && pair.Day == day)
                .Select(pair => new { pair.LowEntryId, pair.HighEntryId, pair.Sequence })
                .ToListAsync(cancellationToken);

            // The last sequence, not the row count: a pair deleted with another player's account leaves a gap, and its number must not be reused.
            var last = served.Count == 0 ? 0 : served.Max(pair => (int)pair.Sequence);
            if (last >= settings.MaxPairsPerDay)
            {
                return await RespondAsync(null, LeagueNoPairReason.DailyLimit);
            }

            var candidates = await CandidatesAsync(db, voterId, day, settings, cancellationToken);
            var sequence = last + 1;
            var choice = Pairing.Choose(candidates, served.Select(pair => (pair.LowEntryId, pair.HighEntryId)).ToHashSet(), voterId, day, sequence);
            if (choice is null)
            {
                return await RespondAsync(null, LeagueNoPairReason.NoPairs);
            }

            var (low, high) = Pairing.Key(choice.LeftId, choice.RightId);
            var pair = new LeaguePair
            {
                Day = day,
                VoterId = voterId,
                LeftEntryId = choice.LeftId,
                RightEntryId = choice.RightId,
                LowEntryId = low,
                HighEntryId = high,
                MatchLevel = choice.Level,
                Sequence = (short)sequence,
                ServedAt = now,
            };

            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                db.LeaguePairs.Add(pair);
                await db.SaveChangesAsync(cancellationToken);

                // Both entries must still compete (one may have been withdrawn since the candidates were read); else choose again. The rows are
                // locked in ID order (as the vote locks them), so two serves or a serve and a vote on the same outfits cannot deadlock. NO KEY
                // UPDATE, the lock an UPDATE of these columns takes anyway: FOR UPDATE would conflict with the KEY SHARE locks the foreign keys of
                // another serve's new pair row hold on the same entries, and two serves would deadlock on that instead.
                var competing = nameof(LeagueEntryStatus.Competing);
                var locked = await db.Database.ExecuteSqlAsync(
                    $"""
                    WITH ordered AS (
                        SELECT id FROM league.entries WHERE id IN ({low}, {high}) AND status = {competing} ORDER BY id FOR NO KEY UPDATE)
                    UPDATE league.entries AS entry
                    SET exposures = entry.exposures + 1, locked_at = COALESCE(entry.locked_at, {now}), updated_at = {now}
                    FROM ordered WHERE entry.id = ordered.id
                    """,
                    cancellationToken);

                if (locked != 2)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                    continue;
                }

                await transaction.CommitAsync(cancellationToken);
                db.ChangeTracker.Clear();

                return await RespondAsync(pair, null);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // A parallel request of the same voter served a pair first (the open-pair and sequence indexes): the next round returns it.
                db.ChangeTracker.Clear();
            }
        }

        return await RespondAsync(null, LeagueNoPairReason.NoPairs);

        async Task<IResult> RespondAsync(LeaguePair? pair, LeagueNoPairReason? reason)
        {
            var stats = await LeagueVoterStats.ForDayAsync(db, voterId, day, cancellationToken);
            LeaguePairResponse? response = null;
            if (pair is not null)
            {
                var pieces = await LeagueResponses.ForVotersAsync(db, storage, [pair.LeftEntryId, pair.RightEntryId], cancellationToken);
                response = new LeaguePairResponse(pair.Id, new LeagueSideResponse(pieces[pair.LeftEntryId]), new LeagueSideResponse(pieces[pair.RightEntryId]), pair.Sequence);
            }

            return TypedResults.Ok(new NextPairResponse(response, reason, stats.Counted, stats.Served, settings.VotesForPoints, settings.MaxPairsPerDay, closesAt));
        }
    }

    /// <summary>
    /// The day's competing entries the voter may see, least exposed first: at most <c>League:PairCandidates</c> human entries and at most as many
    /// engine entries, read as two lists so engine outfits can never crowd every human one out (every pair needs a human A). Not the voter's own,
    /// not one the voter reported, not hidden by reports, not of a player who left, is disabled or whose deletion is pending. Two indexed queries.
    /// </summary>
    private static async Task<List<PairCandidate>> CandidatesAsync(
        KombinDbContext db, Guid voterId, DateOnly day, LeagueOptions settings, CancellationToken cancellationToken)
    {
        var hideAt = settings.Reports.HideAt;
        var visible = db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Day == day && entry.Status == LeagueEntryStatus.Competing && entry.CountingReports < hideAt)
            .Where(entry => entry.UserId == null || entry.UserId != voterId)
            .Where(entry => !db.LeagueReports.Any(report => report.EntryId == entry.Id && report.ReporterId == voterId))
            .Where(entry => entry.UserId == null || db.Users.Any(user => user.Id == entry.UserId && user.DisabledAt == null && user.DeletionRequestedAt == null))
            .Where(entry => entry.UserId == null || db.LeaguePlayers.Any(player => player.UserId == entry.UserId && player.LeftAt == null));

        var humans = await LeastExposedAsync(visible.Where(entry => entry.Kind == LeagueEntryKind.Human));
        var engines = await LeastExposedAsync(visible.Where(entry => entry.Kind == LeagueEntryKind.Engine));

        return [.. humans, .. engines];

        Task<List<PairCandidate>> LeastExposedAsync(IQueryable<LeagueEntry> entries) => entries
            .OrderBy(entry => entry.Exposures)
            .ThenBy(entry => entry.Id)
            .Take(settings.PairCandidates)
            .Select(entry => new PairCandidate(entry.Id, entry.Kind == LeagueEntryKind.Human, entry.Audience, entry.Occasion, entry.Exposures))
            .ToListAsync(cancellationToken);
    }
}
