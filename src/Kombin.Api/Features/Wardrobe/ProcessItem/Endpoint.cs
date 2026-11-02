using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.ProcessItem;

internal sealed record ProcessItemResponse(Guid ItemId, WardrobeItemStatus Status);

internal static class ProcessItemEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/items/{id:guid}/process", Handle)
            .WithName("ProcessWardrobeItem")
            .Produces<ProcessItemResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Enqueues <c>wardrobe-analyze</c>. Idempotent: while the item is processing and the job was enqueued recently, nothing new is
    /// enqueued; a ready item is left alone; a failed one is analysed again from scratch.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IBackgroundJobClient jobs,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var item = await db.WardrobeItems.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == currentUser.UserId, cancellationToken);
        if (item is null)
        {
            return WardrobeErrors.ItemNotFound.ToHttpResult();
        }

        if (item.IsArchived)
        {
            return WardrobeErrors.ItemArchived.ToHttpResult();
        }

        var now = clock.UtcNow;
        var accepted = TypedResults.Accepted($"/api/v1/me/wardrobe/items/{item.Id}", new ProcessItemResponse(item.Id, item.Status));

        if (item.Status == WardrobeItemStatus.Ready
            || (item.Status == WardrobeItemStatus.Processing && item.ProcessRequestedAt > now - WardrobeLimits.ReprocessAfter))
        {
            return accepted;
        }

        if (item.ImageOriginalKey is null)
        {
            return WardrobeErrors.NothingToProcess.ToHttpResult();
        }

        // Fail fast on a missing or oversized upload instead of letting the job find out.
        var size = await storage.GetSizeAsync(item.ImageOriginalKey, cancellationToken);
        if (size is null)
        {
            return WardrobeErrors.UploadMissing.ToHttpResult();
        }

        if (size > WardrobeLimits.MaxImageBytes)
        {
            return WardrobeErrors.ImageTooLarge.ToHttpResult();
        }

        item.Status = WardrobeItemStatus.Processing;
        item.FailureReason = null;
        item.AnalysisAttempts = 0;
        item.ProcessRequestedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        jobs.Enqueue<WardrobeAnalyzeJob>(job => job.RunAsync(item.Id, CancellationToken.None));

        return TypedResults.Accepted($"/api/v1/me/wardrobe/items/{item.Id}", new ProcessItemResponse(item.Id, item.Status));
    }
}
