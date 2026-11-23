using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.BulkAdd;

/// <summary>
/// What confirm, cancel and <c>bulk-add-cleanup</c> share (design note, sections a, b and "Cleanup"): the batch row lock and the deletion of a
/// batch's objects. The objects are found by listing <c>bulk/{user}/{batch}/</c>, never through rows, so a crashed delete or a lost row leaves
/// nothing the next run cannot find.
/// </summary>
internal static class BulkStaging
{
    /// <summary>
    /// The batch, tracked and locked <c>FOR UPDATE</c> until the caller's transaction ends (the lock <c>bulk-add-analyze</c> takes to commit
    /// <c>Ready</c>): a double tap waits for the first confirm, and confirm and cancel race on it. Null when the batch is not the user's.
    /// </summary>
    public static async Task<BulkBatch?> LockAsync(KombinDbContext db, Guid batchId, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.BulkBatches
            .FromSql($"SELECT * FROM wardrobe.bulk_batches WHERE id = {batchId} AND user_id = {userId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return rows.SingleOrDefault();
    }

    /// <summary>The keys a confirm copies a piece's staged images to: the single add's public <c>cutout.png</c> and <c>thumb.webp</c> of the item (piece ID = item ID).</summary>
    public static string[] CopyKeys(Guid userId, Guid itemId) =>
    [
        StorageKeys.Build(WardrobeLimits.StorageArea, userId, itemId, ImageVariant.Cutout, "png"),
        StorageKeys.Build(WardrobeLimits.StorageArea, userId, itemId, ImageVariant.Thumb, "webp"),
    ];

    /// <summary>
    /// Deletes every object under the batch's prefix and records it (<see cref="BulkBatch.ObjectsDeletedAt"/>, no photo key). False when storage
    /// failed: the row keeps <c>objects_deleted_at</c> null and the cleanup tries again. Never throws for a storage failure.
    /// </summary>
    public static async Task<bool> DeleteObjectsAsync(
        KombinDbContext db, IObjectStorage storage, Guid userId, Guid batchId, DateTimeOffset now, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            List<string> keys = [];
            await foreach (var stored in storage.ListAsync(StorageKeys.BulkBatchPrefix(userId, batchId), cancellationToken))
            {
                keys.Add(stored.Key);
            }

            if (keys.Count > 0)
            {
                await storage.DeleteAsync(keys, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BulkStagingLog.DeleteFailed(logger, exception, batchId);
            return false;
        }

        await db.BulkBatches.Where(batch => batch.Id == batchId).ExecuteUpdateAsync(
            set => set
                .SetProperty(batch => batch.ObjectsDeletedAt, now)
                .SetProperty(batch => batch.OriginalKey, (string?)null),
            cancellationToken);

        return true;
    }
}

internal static partial class BulkStagingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Bulk batch {BatchId}: deleting its objects failed; bulk-add-cleanup tries again.")]
    public static partial void DeleteFailed(ILogger logger, Exception exception, Guid batchId);
}
