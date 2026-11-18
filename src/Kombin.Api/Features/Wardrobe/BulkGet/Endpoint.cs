using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Wardrobe.BulkGet;

internal static class BulkGetEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}", Handle)
            .WithName("GetWardrobeBulkBatch")
            .WithSummary("The status of a bulk add: everything the review screen draws.")
            .WithDescription(
                "Poll while status is Processing. Ready carries the pieces with private presigned links (thumbUrl for the grid, imageUrl for the review "
                + "image) that expire at urlsExpireAt: ask again to refresh them. A likelyDuplicate is a hint about the caller's own wardrobe, never a block. "
                + "Every sentence is the server's.")
            .Produces<BulkBatchResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Cheap: one batch row and, once ready, its pieces. Someone else's batch is 404, like one that never existed.</summary>
    private static async Task<IResult> Handle(
        Guid id,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        IOptions<BulkAddOptions> options,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var batch = await db.BulkBatches.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (batch is null)
        {
            return BulkAddErrors.NotFound.ToHttpResult();
        }

        return TypedResults.Ok(await BulkBatchResponses.BuildAsync(db, storage, options.Value, batch, clock.UtcNow, cancellationToken));
    }
}
