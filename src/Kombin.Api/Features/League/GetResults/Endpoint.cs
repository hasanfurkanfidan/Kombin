using System.Globalization;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.GetResults;

/// <summary>The caller's own outfit of that day, as the owner sees it.</summary>
internal sealed record LeagueResultEntryResponse(IReadOnlyList<LeaguePieceResponse> Pieces);

/// <summary>
/// The day's points: from the votes (0-70), voting on ten pairs (0/15), the worn outfit (0/10), a piece added (0/5), and the total shown.
/// While the outfit is under review <c>Votes</c> is null and <c>Total</c> is the usage points only.
/// </summary>
internal sealed record LeaguePointsResponse(int? Votes, int Voting, int Worn, int Pieces, int Total);

/// <summary>
/// <c>Final</c>: the result as the close decided it. <c>UnderReview</c>: the outfit was reported and waits for a review; the rank and the vote
/// points are held back (they are stored and already count in the week). <c>Removed</c>: a review took the outfit out after the close; it has
/// no rank and no vote points.
/// </summary>
internal enum LeagueResultStatus
{
    Final,
    UnderReview,
    Removed,
}

/// <summary>The caller's week of that day (their tier, place in the group, points to the promotion line, when it ends); null when they were in no group.</summary>
internal sealed record LeagueResultsWeekResponse(LeagueTier Tier, int GroupRank, int? PointsToPromotion, DateTimeOffset EndsAt);

/// <summary>
/// The caller's result of one closed day (screen 04, "Akşamki sonuç"). <c>Status</c> and <c>Message</c>: <see cref="LeagueResultStatus"/>;
/// the message is "Kombinin inceleniyor. Sonuç inceleme bitince görünecek." while under review, null otherwise. <c>Entry</c>: their own outfit
/// (null when they had none, or it is older than the entries are kept). <c>Competed</c>: it was in the pool at the close. <c>Ranked</c>: it
/// reached the comparisons to be ranked (null while under review); when false the app says "yeterli oy toplanamadı". <c>Rank</c> (1 = best) of <c>RankedCount</c> ranked outfits; <c>Comparisons</c>: the counted
/// votes on it. <c>PiecePointsAvailable</c>: a piece added now would still earn its points this week ("Eklersen 5 puan"). Nothing about any
/// other player.
/// </summary>
internal sealed record LeagueResultsResponse(
    DateOnly Day,
    LeagueResultStatus Status,
    string? Message,
    LeagueResultEntryResponse? Entry,
    bool Competed,
    bool? Ranked,
    int? Rank,
    int RankedCount,
    int Comparisons,
    LeaguePointsResponse Points,
    bool PiecePointsAvailable,
    LeagueResultsWeekResponse? Week);

internal static class GetResultsEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/results", HandleDay)
            .WithName("GetLeagueResults")
            .Produces<LeagueResultsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/results/latest", HandleLatest)
            .WithName("GetLatestLeagueResults")
            .Produces<LeagueResultsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The caller's result of <paramref name="day"/> (<c>yyyy-MM-dd</c>, required: 400 <c>League.InvalidDay</c> otherwise). 404
    /// <c>League.ResultsNotReady</c> when the day is not closed yet or the caller has no result on it (no entry and no points). Only the caller's
    /// own row is ever read.
    /// </summary>
    private static async Task<IResult> HandleDay(
        string? day, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IOptions<LeagueOptions> options, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return LeagueErrors.InvalidDay.ToHttpResult();
        }

        var userId = currentUser.UserId;
        var score = await db.LeagueDailyScores.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId && row.Day == date, cancellationToken);

        return await RespondAsync(db, storage, options.Value, score, cancellationToken);
    }

    /// <summary>The caller's most recent result (the latest closed day on which they had an entry or earned a point); 404 <c>League.ResultsNotReady</c> when none.</summary>
    private static async Task<IResult> HandleLatest(
        ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IOptions<LeagueOptions> options, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var score = await db.LeagueDailyScores.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderByDescending(row => row.Day)
            .FirstOrDefaultAsync(cancellationToken);

        return await RespondAsync(db, storage, options.Value, score, cancellationToken);
    }

    private static async Task<IResult> RespondAsync(
        KombinDbContext db, IObjectStorage storage, LeagueOptions options, LeagueDailyScore? score, CancellationToken cancellationToken)
    {
        // Rows are written by the close only, but a row is a result only once its day is closed.
        if (score is null || !await db.LeagueDays.AnyAsync(row => row.Day == score.Day && row.ClosedAt != null, cancellationToken))
        {
            return LeagueErrors.ResultsNotReady.ToHttpResult();
        }

        LeagueResultEntryResponse? entry = null;
        if (score.EntryId is { } entryId
            && await db.LeagueEntries.AsNoTracking().Include(row => row.Pieces).SingleOrDefaultAsync(row => row.Id == entryId && row.UserId == score.UserId, cancellationToken) is { } own)
        {
            entry = new LeagueResultEntryResponse(await LeagueResponses.ForOwnerAsync(db, storage, own, cancellationToken));
        }

        // A piece added now (after the close) counts for the next day: available while that day's week has fewer piece days than the cap.
        var next = score.Day.AddDays(1);
        var weekStart = LeagueDays.WeekStart(next);
        var pieceDays = await db.LeagueDailyScores.AsNoTracking()
            .CountAsync(row => row.UserId == score.UserId && row.Day >= weekStart && row.Day < next && row.PiecePoints > 0, cancellationToken);

        var status = score.UnderReview ? LeagueResultStatus.UnderReview : score.RemovedByReview ? LeagueResultStatus.Removed : LeagueResultStatus.Final;
        var held = status == LeagueResultStatus.UnderReview;

        return TypedResults.Ok(new LeagueResultsResponse(
            score.Day,
            status,
            held ? LeagueCopy.UnderReview : null,
            entry,
            score.Competed,
            held ? null : score.Ranked,
            held ? null : score.Rank,
            score.RankedCount,
            score.Comparisons,
            new LeaguePointsResponse(
                held ? null : score.VotePoints,
                score.VotingPoints,
                score.WornPoints,
                score.PiecePoints,
                held ? score.Total - score.VotePoints : score.Total),
            pieceDays < options.Points.PieceDaysPerWeek,
            await LeagueTables.ForMemberAsync(db, options, score.UserId, LeagueDays.WeekStart(score.Day), cancellationToken) is { } week
                ? new LeagueResultsWeekResponse(week.Tier, week.MyRank, week.PointsToPromotion, week.EndsAt)
                : null));
    }
}
