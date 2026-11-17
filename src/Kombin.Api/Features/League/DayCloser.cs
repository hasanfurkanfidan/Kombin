using System.Diagnostics;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Kombin.Api.Features.League;

internal enum DayCloseOutcome
{
    /// <summary>This call closed the day.</summary>
    Closed,

    /// <summary>The day was closed before; nothing changed.</summary>
    AlreadyClosed,

    /// <summary>The day's close time has not come.</summary>
    NotOver,

    /// <summary>Another close holds the lock; nothing was done.</summary>
    AlreadyRunning,
}

/// <summary>What a close did (or found): the day's numbers as stored on <c>league.days</c>, and how many daily score rows it wrote.</summary>
internal sealed record DayCloseResult(
    DayCloseOutcome Outcome,
    DateOnly Day,
    DateTimeOffset? ClosedAt,
    int RankedCount,
    int EngineCount,
    int PairCount,
    int ScoresWritten,
    int BtIterations,
    bool BtConverged,
    int CloseMilliseconds)
{
    public static DayCloseResult Nothing(DayCloseOutcome outcome, DateOnly day) => new(outcome, day, null, 0, 0, 0, 0, 0, false, 0);
}

/// <summary>
/// Closes one league day (design note section 8; the rules, sections 3 and 4): Bradley–Terry over the day's counted votes, the ranking and
/// percentile of the human outfits, and every player's daily points. One transaction, so a close that dies half-way writes nothing; a closed day
/// is never closed again.
/// <list type="number">
/// <item><b>Lock</b>: <c>pg_try_advisory_xact_lock(0x4B4F4D42, 2)</c> (the embedding lock's class, object 2), released by the commit or the
/// rollback; not getting it means another close is running (<see cref="DayCloseOutcome.AlreadyRunning"/>). Then the day row, created when missing
/// (a day nobody voted on has none; the first vote creates it open, <c>closed_at</c> null), is read <c>FOR UPDATE</c>: a vote holds it
/// <c>FOR SHARE</c>, so a vote and the close never interleave. A row with <c>closed_at</c> set is <see cref="DayCloseOutcome.AlreadyClosed"/>.</item>
/// <item><b>Who is left out</b>: entries of a player who is disabled or whose deletion is pending, the votes those players cast, and every vote on
/// a <c>Removed</c> entry (a review took it out after reports: a win or a loss against it says nothing reliable about the other outfit).</item>
/// <item><b>The votes</b>: the day's pair rows that counted, not a skip and with a weight (the entry counters are only the win rate shown after
/// a vote). Withdrawn entries stay in the fit as opponents (section 4: their comparisons are information about the other outfits) but are never
/// ranked; engine entries likewise.</item>
/// <item><b>Ranked</b>: a human entry still <c>Competing</c> with at least <c>MinComparisons</c> of those votes. The rank and percentile are
/// <see cref="Percentile.Rank"/>'s, the strengths <see cref="BradleyTerry.Fit"/>'s. An entry hidden by reports (<c>counting_reports ≥
/// Reports:HideAt</c>) and not reviewed is ranked like any other (task L5: leaving it unranked let a player wipe a rival's vote points by
/// reporting it); its row carries <c>under_review</c>, its points are stored and count in the week, and its owner sees "under review" until
/// <see cref="LeagueEntryReviewer"/> restores or removes it.</item>
/// <item><b>Rows</b>: one <c>daily_scores</c> row per player joined at the close (joined before it, not left before it, not disabled, no deletion
/// pending) who had an entry that day or earned a point (<see cref="DailyPoints.For"/>). <c>competed</c> is true for every entry still
/// <c>Competing</c> at the close, ranked or not: the repeat rule reads it. The voting points come from the voter's own counter
/// (<c>league.voter_days</c>), so pairs deleted with another account during the day cost the voter nothing.</item>
/// <item><b>Writes</b>: the entries' strength, percentile and rank, the score rows (one statement each, from arrays), the group placement of every
/// player with a row who is in no group of the week yet (<see cref="LeagueGroups.PlaceAsync"/>, task L5), the engine preference rows (no user ID),
/// the day row's numbers and <c>closed_at</c>. Then the commit.</item>
/// </list>
/// Determinism: entries and pairs are read in ID order, the fit and the ranking are pure and ordered, and nothing depends on the clock but
/// <c>closed_at</c> and <c>close_ms</c>, so the same rows give the same strengths, percentiles, ranks and points.
/// </summary>
internal sealed partial class DayCloser(KombinDbContext db, IOptions<LeagueOptions> options, ILogger<DayCloser> logger)
{
    /// <summary>The advisory lock's class (shared with the embedding run's lock) and the close's object within it.</summary>
    public const int LockClass = 0x4B4F4D42;

    public const int LockObject = 2;

    public async Task<DayCloseResult> CloseAsync(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (now < LeagueDays.ClosesAt(day, settings.DayClosesAt))
        {
            return DayCloseResult.Nothing(DayCloseOutcome.NotOver, day);
        }

        var stopwatch = Stopwatch.StartNew();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var locked = (await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockClass}, {LockObject}) AS \"Value\"").ToListAsync(cancellationToken)).Single();
        if (!locked)
        {
            await transaction.RollbackAsync(cancellationToken);
            return DayCloseResult.Nothing(DayCloseOutcome.AlreadyRunning, day);
        }

        var defaultClose = LeagueDays.ClosesAt(day, settings.DayClosesAt);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO league.days (day, closes_at, ranked_count, engine_count, pair_count, bt_iterations, bt_converged, close_ms) VALUES ({day}, {defaultClose}, 0, 0, 0, 0, false, 0) ON CONFLICT (day) DO NOTHING",
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.days WHERE day = {day} FOR UPDATE", cancellationToken);
        var dayRow = await db.LeagueDays.SingleAsync(row => row.Day == day, cancellationToken);

        if (dayRow.ClosedAt is not null || now < dayRow.ClosesAt)
        {
            await transaction.CommitAsync(cancellationToken);

            return dayRow.ClosedAt is null
                ? DayCloseResult.Nothing(DayCloseOutcome.NotOver, day)
                : new DayCloseResult(DayCloseOutcome.AlreadyClosed, day, dayRow.ClosedAt, dayRow.RankedCount, dayRow.EngineCount, dayRow.PairCount, 0,
                    dayRow.BtIterations, dayRow.BtConverged, dayRow.CloseMilliseconds);
        }

        var closesAt = dayRow.ClosesAt;
        var input = await LoadAsync(day, closesAt, settings, cancellationToken);
        var outcome = Compute(input, settings);

        await WriteEntriesAsync(input, outcome, now, cancellationToken);
        await WriteScoresAsync(day, outcome, cancellationToken);

        // Everyone with a result this day is in a group of its week (a player who only wore an outfit and never opened the league screen too).
        await LeagueGroups.PlaceAsync(db, settings, LeagueDays.WeekStart(day), [.. outcome.Scores.Select(score => score.UserId)], now, cancellationToken);
        db.LeagueEnginePreferences.AddRange(outcome.EnginePreferences);

        dayRow.ClosedAt = now;
        dayRow.RankedCount = outcome.Ranked.Count;
        dayRow.EngineCount = input.Entries.Count(entry => entry.Kind == LeagueEntryKind.Engine);
        dayRow.PairCount = outcome.Games;
        dayRow.BtIterations = outcome.Fit.Iterations;
        dayRow.BtConverged = outcome.Fit.Converged;
        dayRow.CloseMilliseconds = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (!outcome.Fit.Converged)
        {
            LogNotConverged(logger, day, outcome.Fit.Iterations);
        }

        LogClosed(logger, day, outcome.Ranked.Count, dayRow.EngineCount, outcome.Games, outcome.Scores.Count, dayRow.CloseMilliseconds);

        return new DayCloseResult(DayCloseOutcome.Closed, day, now, outcome.Ranked.Count, dayRow.EngineCount, outcome.Games, outcome.Scores.Count,
            outcome.Fit.Iterations, outcome.Fit.Converged, dayRow.CloseMilliseconds);
    }

    /// <summary>The rule of the close, apart from the I/O: who is in the fit, the fit, the ranking, the points. Pure.</summary>
    internal static CloseOutcome Compute(CloseInput input, LeagueOptions settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);

        // In the fit: competing and withdrawn entries (and engine ones), never removed ones, never a left-out player's.
        var participants = input.Entries
            .Where(entry => entry.Status is LeagueEntryStatus.Competing or LeagueEntryStatus.Withdrawn)
            .Where(entry => entry.UserId is not { } owner || !input.LeftOut.Contains(owner))
            .ToList();
        var index = participants.Select((entry, position) => (entry.Id, position)).ToDictionary(pair => pair.Id, pair => pair.position);

        var games = new List<BtGame>();
        var comparisons = new int[participants.Count];
        var preferences = new List<LeagueEnginePreference>();

        foreach (var pair in input.Pairs)
        {
            if (pair.Choice == LeaguePairChoice.Skip || pair.Weight <= 0 || input.LeftOut.Contains(pair.VoterId)
                || !index.TryGetValue(pair.LeftEntryId, out var left) || !index.TryGetValue(pair.RightEntryId, out var right))
            {
                continue;
            }

            var (winner, loser) = pair.Choice == LeaguePairChoice.Left ? (left, right) : (right, left);
            games.Add(new BtGame(winner, loser, pair.Weight));
            comparisons[left]++;
            comparisons[right]++;

            if (EnginePreference(input.Day, participants[winner], participants[loser], pair.Weight) is { } preference)
            {
                preferences.Add(preference);
            }
        }

        var fit = BradleyTerry.Fit(participants.Count, games, settings.PriorWins, settings.PriorLosses, settings.BtTolerance, settings.BtMaxIterations);

        var rankable = participants
            .Select((entry, position) => (entry, position))
            .Where(item => item.entry.Kind == LeagueEntryKind.Human && item.entry.Status == LeagueEntryStatus.Competing
                && comparisons[item.position] >= settings.MinComparisons)
            .Select(item => (item.entry.Id, fit.Strengths[item.position]))
            .ToList();
        var ranked = Percentile.Rank(rankable).ToDictionary(outfit => outfit.EntryId);

        var entriesByPlayer = input.Entries.Where(entry => entry.UserId is not null).ToDictionary(entry => entry.UserId!.Value);
        var scores = new List<LeagueDailyScore>();
        foreach (var player in input.Players)
        {
            var entry = entriesByPlayer.GetValueOrDefault(player);
            var competed = entry?.Status == LeagueEntryStatus.Competing;
            var position = entry is not null && index.TryGetValue(entry.Id, out var found) ? found : -1;
            var outfit = entry is not null ? ranked.GetValueOrDefault(entry.Id) : null;
            var points = DailyPoints.For(
                new DailyActivity(outfit, input.CountedVotes.GetValueOrDefault(player), input.Worn.Contains(player), input.PieceAdded.Contains(player),
                    input.PieceDaysBefore.GetValueOrDefault(player)),
                settings.Points,
                settings.VotesForPoints);

            if (entry is null && points.Total == 0)
            {
                continue;
            }

            scores.Add(new LeagueDailyScore
            {
                UserId = player,
                Day = input.Day,
                EntryId = entry?.Id,
                Competed = competed,
                Ranked = outfit is not null,
                Rank = outfit?.Rank,
                RankedCount = ranked.Count,
                Comparisons = position >= 0 ? comparisons[position] : 0,
                Percentile = outfit?.Value,
                VotePoints = points.Votes,
                VotingPoints = points.Voting,
                WornPoints = points.Worn,
                PiecePoints = points.Piece,
                Total = points.Total,
                UnderReview = competed && entry!.CountingReports >= settings.Reports.HideAt,
            });
        }

        var strengths = participants.Select((entry, position) => (entry.Id, Strength: fit.Strengths[position], Ranked: ranked.GetValueOrDefault(entry.Id))).ToList();

        return new CloseOutcome(fit, games.Count, [.. ranked.Values], strengths, scores, preferences);
    }

    /// <summary>A counted vote between an engine outfit and a human one, kept with the engine outfit's products only (never a player's pieces).</summary>
    private static LeagueEnginePreference? EnginePreference(DateOnly day, CloseEntry winner, CloseEntry loser, double weight)
    {
        var engine = winner.Kind == LeagueEntryKind.Engine ? winner : loser.Kind == LeagueEntryKind.Engine ? loser : null;
        if (engine is null || (winner.Kind == LeagueEntryKind.Engine && loser.Kind == LeagueEntryKind.Engine))
        {
            return null;
        }

        var won = engine == winner;

        return new LeagueEnginePreference
        {
            Day = day,
            Audience = engine.Audience,
            Occasion = engine.Occasion,
            WinnerProducts = won ? engine.PieceIds : [],
            LoserProducts = won ? [] : engine.PieceIds,
            Weight = (float)weight,
        };
    }

    private async Task<CloseInput> LoadAsync(DateOnly day, DateTimeOffset closesAt, LeagueOptions settings, CancellationToken cancellationToken)
    {
        var entries = await db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Day == day)
            .OrderBy(entry => entry.Id)
            .Select(entry => new CloseEntry(entry.Id, entry.UserId, entry.Kind, entry.Status, entry.CountingReports, entry.Audience, entry.Occasion, entry.PieceIds))
            .ToListAsync(cancellationToken);

        var pairs = await db.LeaguePairs.AsNoTracking()
            .Where(pair => pair.Day == day && pair.Counted && pair.Weight > 0 && pair.Choice != null && pair.Choice != LeaguePairChoice.Skip)
            .OrderBy(pair => pair.Id)
            .Select(pair => new ClosePair(pair.VoterId, pair.LeftEntryId, pair.RightEntryId, pair.Choice!.Value, pair.Weight))
            .ToListAsync(cancellationToken);

        // Disabled players and players whose deletion is pending: neither their outfits nor their votes count.
        var leftOut = await db.Users.AsNoTracking()
            .Where(user => user.DisabledAt != null || user.DeletionRequestedAt != null)
            .Where(user => db.LeagueEntries.Any(entry => entry.Day == day && entry.UserId == user.Id) || db.LeagueVoterDays.Any(voter => voter.Day == day && voter.UserId == user.Id))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        // Joined at the close: joined before it, not left before it, in good standing. A player who left gets no row (section 9).
        var players = await db.LeaguePlayers.AsNoTracking()
            .Where(player => player.JoinedAt < closesAt && (player.LeftAt == null || player.LeftAt >= closesAt))
            .Where(player => db.Users.Any(user => user.Id == player.UserId && user.DisabledAt == null && user.DeletionRequestedAt == null))
            .OrderBy(player => player.UserId)
            .Select(player => player.UserId)
            .ToListAsync(cancellationToken);

        var countedVotes = await db.LeagueVoterDays.AsNoTracking()
            .Where(voter => voter.Day == day && voter.Counted > 0)
            .ToDictionaryAsync(voter => voter.UserId, voter => voter.Counted, cancellationToken);

        var dayStart = LeagueDays.Start(day);
        var worn = await db.OutfitFeedback.AsNoTracking()
            .Where(feedback => feedback.Action == OutfitFeedbackAction.Worn && feedback.CreatedAt >= dayStart && feedback.CreatedAt < closesAt)
            .Where(feedback => db.LeaguePlayers.Any(player => player.UserId == feedback.UserId))
            .Select(feedback => feedback.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var (pieceStart, _) = LeagueDays.PieceWindow(day, settings.DayClosesAt);
        var pieceAdded = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.CreatedAt >= pieceStart && item.CreatedAt < closesAt && item.Status == WardrobeItemStatus.Ready && !item.IsArchived
                && !item.Warnings.Contains(WardrobeItem.NotGarmentWarning))
            .Where(item => db.LeaguePlayers.Any(player => player.UserId == item.UserId))
            .Select(item => item.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var weekStart = LeagueDays.WeekStart(day);
        var pieceDaysBefore = await db.LeagueDailyScores.AsNoTracking()
            .Where(score => score.Day >= weekStart && score.Day < day && score.PiecePoints > 0)
            .GroupBy(score => score.UserId)
            .Select(group => new { UserId = group.Key, Days = group.Count() })
            .ToDictionaryAsync(row => row.UserId, row => row.Days, cancellationToken);

        return new CloseInput(day, entries, pairs, leftOut.ToHashSet(), players, countedVotes, worn.ToHashSet(), pieceAdded.ToHashSet(), pieceDaysBefore);
    }

    /// <summary>Strength for every entry in the fit, percentile and rank for the ranked ones: one statement over arrays.</summary>
    private async Task WriteEntriesAsync(CloseInput input, CloseOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (outcome.Strengths.Count == 0)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE league.entries AS entry
            SET strength = fitted.strength, percentile = fitted.percentile, rank = fitted.rank, updated_at = @now
            FROM unnest(@ids, @strengths, @percentiles, @ranks) AS fitted(id, strength, percentile, rank)
            WHERE entry.id = fitted.id AND entry.day = @day
            """,
            [
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("day", input.Day),
                ArrayOf("ids", NpgsqlDbType.Uuid, outcome.Strengths.Select(row => row.EntryId).ToArray()),
                ArrayOf("strengths", NpgsqlDbType.Double, outcome.Strengths.Select(row => row.Strength).ToArray()),
                ArrayOf("percentiles", NpgsqlDbType.Double, outcome.Strengths.Select(row => row.Ranked?.Value).ToArray()),
                ArrayOf("ranks", NpgsqlDbType.Integer, outcome.Strengths.Select(row => row.Ranked?.Rank).ToArray()),
            ],
            cancellationToken);
    }

    /// <summary>The day's score rows, inserted in one statement from arrays (10,000 players do not go through the change tracker).</summary>
    private async Task WriteScoresAsync(DateOnly day, CloseOutcome outcome, CancellationToken cancellationToken)
    {
        var scores = outcome.Scores;
        if (scores.Count == 0)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO league.daily_scores
                (user_id, day, entry_id, competed, ranked, rank, ranked_count, comparisons, percentile, vote_points, voting_points, worn_points, piece_points, total,
                 under_review, removed_by_review)
            SELECT row.user_id, @day, row.entry_id, row.competed, row.ranked, row.rank, row.ranked_count, row.comparisons, row.percentile,
                   row.vote_points, row.voting_points, row.worn_points, row.piece_points, row.total, row.under_review, false
            FROM unnest(@users, @entries, @competed, @ranked, @ranks, @rankedCounts, @comparisons, @percentiles, @votes, @voting, @worn, @pieces, @totals, @underReview)
                AS row(user_id, entry_id, competed, ranked, rank, ranked_count, comparisons, percentile, vote_points, voting_points, worn_points, piece_points, total,
                       under_review)
            """,
            [
                new NpgsqlParameter("day", day),
                ArrayOf("users", NpgsqlDbType.Uuid, scores.Select(score => score.UserId).ToArray()),
                ArrayOf("entries", NpgsqlDbType.Uuid, scores.Select(score => score.EntryId).ToArray()),
                ArrayOf("competed", NpgsqlDbType.Boolean, scores.Select(score => score.Competed).ToArray()),
                ArrayOf("ranked", NpgsqlDbType.Boolean, scores.Select(score => score.Ranked).ToArray()),
                ArrayOf("ranks", NpgsqlDbType.Integer, scores.Select(score => score.Rank).ToArray()),
                ArrayOf("rankedCounts", NpgsqlDbType.Integer, scores.Select(score => score.RankedCount).ToArray()),
                ArrayOf("comparisons", NpgsqlDbType.Integer, scores.Select(score => score.Comparisons).ToArray()),
                ArrayOf("percentiles", NpgsqlDbType.Double, scores.Select(score => score.Percentile).ToArray()),
                ArrayOf("votes", NpgsqlDbType.Integer, scores.Select(score => score.VotePoints).ToArray()),
                ArrayOf("voting", NpgsqlDbType.Integer, scores.Select(score => score.VotingPoints).ToArray()),
                ArrayOf("worn", NpgsqlDbType.Integer, scores.Select(score => score.WornPoints).ToArray()),
                ArrayOf("pieces", NpgsqlDbType.Integer, scores.Select(score => score.PiecePoints).ToArray()),
                ArrayOf("totals", NpgsqlDbType.Integer, scores.Select(score => score.Total).ToArray()),
                ArrayOf("underReview", NpgsqlDbType.Boolean, scores.Select(score => score.UnderReview).ToArray()),
            ],
            cancellationToken);
    }

    private static NpgsqlParameter ArrayOf<T>(string name, NpgsqlDbType elementType, T[] values) =>
        new(name, NpgsqlDbType.Array | elementType) { Value = values };

    [LoggerMessage(Level = LogLevel.Information, Message = "league-close: {Day} closed: {Ranked} ranked, {Engine} engine outfits, {Pairs} counted votes, {Scores} score rows, {Milliseconds} ms.")]
    private static partial void LogClosed(ILogger logger, DateOnly day, int ranked, int engine, int pairs, int scores, int milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "league-close: the Bradley–Terry fit of {Day} did not converge in {Iterations} iterations; the last strengths are used.")]
    private static partial void LogNotConverged(ILogger logger, DateOnly day, int iterations);
}

/// <summary>One entry of the day as the close reads it.</summary>
internal sealed record CloseEntry(
    Guid Id, Guid? UserId, LeagueEntryKind Kind, LeagueEntryStatus Status, int CountingReports, Audience Audience, Occasion? Occasion, Guid[] PieceIds);

/// <summary>One counted vote of the day: who voted, the two sides and the choice (never a skip), the weight.</summary>
internal sealed record ClosePair(Guid VoterId, Guid LeftEntryId, Guid RightEntryId, LeaguePairChoice Choice, double Weight);

/// <summary>
/// Everything the close reads: the day's entries and counted votes in ID order, the players left out (disabled or deletion pending), the players
/// joined at the close in ID order, each voter's counted votes of the day, who wore an outfit, who added a piece, and the earlier piece days of
/// the week.
/// </summary>
internal sealed record CloseInput(
    DateOnly Day,
    IReadOnlyList<CloseEntry> Entries,
    IReadOnlyList<ClosePair> Pairs,
    IReadOnlySet<Guid> LeftOut,
    IReadOnlyList<Guid> Players,
    IReadOnlyDictionary<Guid, int> CountedVotes,
    IReadOnlySet<Guid> Worn,
    IReadOnlySet<Guid> PieceAdded,
    IReadOnlyDictionary<Guid, int> PieceDaysBefore);

/// <summary>What the close computed: the fit, the number of votes in it, the ranking, the strengths to store, the score rows, the engine rows.</summary>
internal sealed record CloseOutcome(
    BtFit Fit,
    int Games,
    IReadOnlyList<RankedOutfit> Ranked,
    IReadOnlyList<(Guid EntryId, double Strength, RankedOutfit? Ranked)> Strengths,
    IReadOnlyList<LeagueDailyScore> Scores,
    IReadOnlyList<LeagueEnginePreference> EnginePreferences);
