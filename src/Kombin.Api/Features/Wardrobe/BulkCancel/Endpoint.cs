using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.BulkCancel;

internal static class BulkCancelEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}", Handle)
            .WithName("CancelWardrobeBulkBatch")
            .WithSummary("Cancels a bulk add: the pieces and every staged image are deleted. Idempotent.")
            .WithDescription("A cancelled or expired batch answers 204 again; a confirmed one 409 BulkAlreadyConfirmed.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Under the batch row lock (the one confirm takes: first wins, the other is 409): <c>Cancelled</c>, the pieces deleted; after the commit the
    /// batch's objects (the room photo of a failed batch, the staged images) are deleted by their prefix. A job still analysing the photo finds the
    /// batch no longer <c>Processing</c> and writes nothing.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var batch = await BulkStaging.LockAsync(db, id, userId, cancellationToken);
            if (batch is null)
            {
                return BulkAddErrors.NotFound.ToHttpResult();
            }

            if (batch.Status == BulkBatchStatus.Confirmed)
            {
                return BulkAddErrors.AlreadyConfirmed.ToHttpResult();
            }

            if (batch.Status is BulkBatchStatus.Cancelled or BulkBatchStatus.Expired)
            {
                // Already over; a repeat only finishes a deletion of the objects that failed the first time.
                if (batch.ObjectsDeletedAt is not null)
                {
                    return TypedResults.NoContent();
                }
            }
            else
            {
                await db.BulkPieces.Where(piece => piece.BatchId == id).ExecuteDeleteAsync(cancellationToken);
                batch.Status = BulkBatchStatus.Cancelled;
                batch.EndedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        db.ChangeTracker.Clear();
        await BulkStaging.DeleteObjectsAsync(db, storage, userId, id, now, loggers.CreateLogger(typeof(BulkCancelEndpoint)), cancellationToken);

        return TypedResults.NoContent();
    }
}
