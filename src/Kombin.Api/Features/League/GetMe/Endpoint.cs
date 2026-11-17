using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.GetMe;

/// <summary>The consent text the join screen shows, and its version (sent back by <c>POST /league/join</c>).</summary>
internal sealed record LeagueConsentResponse(string Version, string Text);

/// <summary>
/// The caller's outfit in today's league. <c>Status</c>: Pending (waiting for the image safety check), Competing, NotCompeting (with a
/// <c>Reason</c>), Withdrawn, Removed. <c>Message</c> is the Turkish line to show (<c>LeagueCopy</c>): one for every <c>Reason</c> of a NotCompeting entry
/// (RecentlyCompeted, UnsafePiece, IncompleteOutfit, PieceUnavailable, AgeRestricted, NotWorn) and one for Removed; null for Pending, Competing
/// and Withdrawn. The pieces are the owner's own view.
/// </summary>
internal sealed record LeagueTodayEntryResponse(LeagueEntryStatus Status, LeagueEntryReason? Reason, string? Message, IReadOnlyList<LeaguePieceResponse> Pieces);

/// <summary>
/// Today in the league (an Istanbul day). <c>ClosesAt</c>: votes stop and the results come (21:00 Istanbul). <c>Entry</c>: the caller's outfit,
/// null when no worn outfit entered. <c>ServedPairs</c> / <c>CountedVotes</c>: the caller's pairs today and the votes that counted; <c>Target</c>
/// counted votes earn the voting points, <c>Limit</c> pairs is the day's cap.
/// </summary>
internal sealed record LeagueTodayResponse(
    DateOnly Day, DateTimeOffset ClosesAt, LeagueTodayEntryResponse? Entry, int ServedPairs, int CountedVotes, int Target, int Limit);

/// <summary>
/// The caller's week in their group: place, group size, week points so far (the best five closed days), points to the promotion line ("5. sıraya
/// 17 puan"; null inside the band or without one) and when the week ends. Null for a player in no group this week.
/// </summary>
internal sealed record LeagueWeekResponse(int GroupRank, int GroupSize, int Points, int? PointsToPromotion, DateTimeOffset EndsAt);

/// <summary>
/// The caller's league state. <c>Joined</c>: in the league now. <c>ConsentRequired</c>: the join screen must be shown (never joined, left, or
/// joined under an older consent version). <c>Tier</c> is null for someone who never joined; a player who left keeps theirs. <c>Today</c>: the
/// day's entry and votes. <c>Week</c>: the caller's group this week (null when not in one).
/// </summary>
internal sealed record LeagueMeResponse(
    bool Joined,
    bool ConsentRequired,
    LeagueConsentResponse Consent,
    LeagueTier? Tier,
    DateTimeOffset? JoinedAt,
    LeagueTodayResponse Today,
    LeagueWeekResponse? Week);

internal static partial class GetLeagueMeEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/me", Handle)
            .WithName("GetLeagueMe")
            .Produces<LeagueMeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>The state of <paramref name="userId"/> at <paramref name="now"/>; <c>POST /league/join</c> answers with it too.</summary>
    public static async Task<LeagueMeResponse> BuildAsync(
        KombinDbContext db, IObjectStorage storage, LeagueOptions options, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var player = await db.LeaguePlayers.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        var current = LeagueConsent.Current;
        var joined = player is { IsJoined: true };

        return new LeagueMeResponse(
            joined,
            !joined || player!.ConsentVersion != current.Version,
            new LeagueConsentResponse(current.Version, current.Text),
            player?.Tier,
            joined ? player!.JoinedAt : null,
            await TodayAsync(db, storage, options, userId, now, cancellationToken),
            joined ? await WeekAsync(db, options, userId, now, cancellationToken) : null);
    }

    private static async Task<LeagueWeekResponse?> WeekAsync(KombinDbContext db, LeagueOptions options, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LeagueGroups.PlaceOneAsync(db, options, userId, now, cancellationToken);
        var view = await LeagueTables.ForMemberAsync(db, options, userId, LeagueDays.WeekStart(Istanbul.Today(now)), cancellationToken);

        return view is null ? null : new LeagueWeekResponse(view.MyRank, view.Rows.Count, view.MyPoints, view.PointsToPromotion, view.EndsAt);
    }

    private static async Task<LeagueTodayResponse> TodayAsync(
        KombinDbContext db, IObjectStorage storage, LeagueOptions options, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var day = Istanbul.Today(now);
        var entry = await db.LeagueEntries.AsNoTracking()
            .Include(row => row.Pieces)
            .SingleOrDefaultAsync(row => row.Day == day && row.UserId == userId, cancellationToken);
        var votes = await LeagueVoterStats.ForDayAsync(db, userId, day, cancellationToken);

        return new LeagueTodayResponse(
            day,
            LeagueDays.ClosesAt(day, options.DayClosesAt),
            entry is null
                ? null
                : new LeagueTodayEntryResponse(entry.Status, entry.Reason, LeagueCopy.ForEntry(entry.Status, entry.Reason), await LeagueResponses.ForOwnerAsync(db, storage, entry, cancellationToken)),
            votes.Served,
            votes.Counted,
            options.VotesForPoints,
            options.MaxPairsPerDay);
    }

    /// <summary>
    /// Resolves the caller's outfit of the day first (the owner never waits for the <c>league-entries</c> job to see their status; the image
    /// safety filter is not asked here, so a piece without a verdict shows <c>Pending</c> until the job asks). A failure there is logged and the
    /// state is shown as it is.
    /// </summary>
    private static async Task<IResult> Handle(
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        EntryMaterializer materializer,
        IClock clock,
        IOptions<LeagueOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        try
        {
            await materializer.ResolveAsync(userId, now, moderationBudget: 0, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogResolveFailed(loggers.CreateLogger(typeof(GetLeagueMeEndpoint)), exception);
            db.ChangeTracker.Clear();
        }

        return TypedResults.Ok(await BuildAsync(db, storage, options.Value, userId, now, cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "League: resolving the caller's entry failed; the state is shown as it is.")]
    private static partial void LogResolveFailed(ILogger logger, Exception exception);
}
