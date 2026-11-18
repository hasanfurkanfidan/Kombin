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
    public const int Limit = LeagueReviewQueue.Limit;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/review-queue", Handle)
            .WithName("GetLeagueReviewQueue")
            .Produces<IReadOnlyList<LeagueReviewItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Every player outfit with a report since its last review that is not removed (design note section 13: every reported outfit is here, hidden
    /// or not), the oldest such report first: <see cref="LeagueReviewQueue"/>, which the admin panel's league page reads too.
    /// </summary>
    private static async Task<IResult> Handle(KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var queue = await LeagueReviewQueue.ListAsync(db, storage, cancellationToken);

        return TypedResults.Ok<IReadOnlyList<LeagueReviewItem>>(
            [.. queue.Select(item => new LeagueReviewItem(item.EntryId, item.Day, item.Status, item.CountingReports, item.Reports, item.UnderReview, item.Pieces))]);
    }
}
