using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League.Internal.ReviewQueue;

/// <summary>
/// One reported outfit waiting for a review: its entry ID (operators only; never shown to players), its day and status, the reports that count
/// toward the hide and every report since the last review, whether a closed day's result is held back for it (<c>UnderReview</c>), and the pieces
/// as voters see them.
/// </summary>
internal sealed record LeagueReviewItem(
    Guid EntryId, DateOnly Day, LeagueEntryStatus Status, int CountingReports, int Reports, bool UnderReview, IReadOnlyList<LeaguePieceResponse> Pieces);

internal static class ReviewQueueEndpoint
{
    /// <summary>At most this many outfits per call, oldest report first.</summary>
    public const int Limit = 100;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/review-queue", Handle)
            .WithName("GetLeagueReviewQueue")
            .Produces<IReadOnlyList<LeagueReviewItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Every player outfit with a report since its last review that is not removed (design note section 13: every reported outfit is here, hidden
    /// or not), the oldest such report first.
    /// </summary>
    private static async Task<IResult> Handle(KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var items = await db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Kind == LeagueEntryKind.Human && entry.Status != LeagueEntryStatus.Removed)
            .Select(entry => new
            {
                entry.Id,
                entry.Day,
                entry.Status,
                entry.CountingReports,
                Reports = db.LeagueReports.Count(report => report.EntryId == entry.Id && (entry.ReviewedAt == null || report.CreatedAt > entry.ReviewedAt)),
                First = db.LeagueReports
                    .Where(report => report.EntryId == entry.Id && (entry.ReviewedAt == null || report.CreatedAt > entry.ReviewedAt))
                    .Min(report => (DateTimeOffset?)report.CreatedAt),
                UnderReview = db.LeagueDailyScores.Any(score => score.EntryId == entry.Id && score.UnderReview),
            })
            .Where(entry => entry.Reports > 0)
            .OrderBy(entry => entry.First)
            .ThenBy(entry => entry.Id)
            .Take(Limit)
            .ToListAsync(cancellationToken);

        var pieces = await LeagueResponses.ForVotersAsync(db, storage, [.. items.Select(item => item.Id)], cancellationToken);

        return TypedResults.Ok<IReadOnlyList<LeagueReviewItem>>(
            [.. items.Select(item => new LeagueReviewItem(item.Id, item.Day, item.Status, item.CountingReports, item.Reports, item.UnderReview, pieces[item.Id]))]);
    }
}
