using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Wardrobe.Jobs;

/// <summary>
/// The copies a confirm wrote under its items' keys (<c>wardrobe/{user}/{piece}/cutout.png</c>, <c>thumb.webp</c>) and could not delete itself when it
/// failed or was aborted (design note, "PR 3 as built"). They are not under <c>bulk/</c>, so <c>bulk-add-cleanup</c> cannot find them, and no item row
/// names them, so the account purge cannot either: Hangfire's job store keeps the keys until a run succeeds (a failing store throws, and Hangfire tries
/// again). Under the batch row lock (a confirm holds it while it copies and commits) only the keys of pieces that have no wardrobe item are deleted: a
/// later confirm that succeeded wrote the same keys, and they stay.
/// </summary>
internal sealed class BulkAddDiscardCopiesJob(KombinDbContext db, IObjectStorage storage)
{
    public const string JobId = "bulk-add-discard-copies";

    [AutomaticRetry(Attempts = 10)]
    [JobDisplayName("bulk-add-discard-copies {0}")]
    public async Task RunAsync(Guid batchId, Guid userId, Guid[] itemIds, CancellationToken cancellationToken) =>
        await DiscardAsync(batchId, userId, itemIds, cancellationToken);

    /// <summary>Deletes the copies of <paramref name="itemIds"/> that have no item; returns how many keys it deleted.</summary>
    public async Task<int> DiscardAsync(Guid batchId, Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Null when the batch is gone (the tombstone expired, the account was purged): then no confirm can run any more.
        await BulkStaging.LockAsync(db, batchId, userId, cancellationToken);

        var ids = itemIds.ToList();
        var items = await db.WardrobeItems.Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(cancellationToken);
        List<string> keys = [.. ids.Except(items).SelectMany(id => BulkStaging.CopyKeys(userId, id))];
        if (keys.Count > 0)
        {
            await storage.DeleteAsync(keys, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return keys.Count;
    }
}
