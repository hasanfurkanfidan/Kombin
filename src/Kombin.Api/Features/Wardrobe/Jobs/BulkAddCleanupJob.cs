using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Wardrobe.Jobs;

internal readonly record struct BulkCleanupSummary(int Interrupted, int Expired, int BatchesEmptied, int OrphansDeleted, int TombstonesDeleted);

/// <summary>
/// Hourly at :17 Istanbul (design note, "Cleanup"). In order: a batch still <c>Processing</c> after <c>BulkAdd:InterruptedAfter</c> is <c>Failed</c>
/// <c>Interrupted</c> (the job that held it died; retryable); an unfinished batch past its <c>expires_at</c> is <c>Expired</c> and the pieces of every
/// ended batch are deleted; the objects of every ended batch not yet emptied are deleted by the batch's prefix; objects under <c>bulk/</c> whose batch
/// row is gone or ended (a crashed delete, a late write) are deleted; tombstones older than <c>BulkAdd:TombstoneRetention</c> are deleted. Registered
/// even when <c>BulkAdd:Enabled</c> is false (staging must disappear when the flag is switched off), and never looks at the flag. Idempotent.
/// </summary>
internal sealed partial class BulkAddCleanupJob(
    KombinDbContext db, IObjectStorage storage, IClock clock, IOptions<BulkAddOptions> options, ILogger<BulkAddCleanupJob> logger)
{
    public const string JobId = "bulk-add-cleanup";

    /// <summary>Hourly at :17 in Europe/Istanbul (the zone is set where the job is registered).</summary>
    public const string Cron = "17 * * * *";

    public const int BatchSize = 200;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await CleanupAsync(null, clock.UtcNow, cancellationToken);

        LogRun(logger, summary.Interrupted, summary.Expired, summary.BatchesEmptied, summary.OrphansDeleted, summary.TombstonesDeleted);
    }

    /// <summary><paramref name="userId"/> restricts the run to one user's batches and objects (tests share a database and a store).</summary>
    public async Task<BulkCleanupSummary> CleanupAsync(Guid? userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var mine = db.BulkBatches.Where(batch => userId == null || batch.UserId == userId);

        var interruptedBefore = now - settings.InterruptedAfter;
        var interrupted = await mine
            .Where(batch => batch.Status == BulkBatchStatus.Processing && batch.ProcessRequestedAt <= interruptedBefore)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(batch => batch.Status, BulkBatchStatus.Failed)
                    .SetProperty(batch => batch.FailureCode, BulkFailureCode.Interrupted)
                    .SetProperty(batch => batch.FailureDetail, "interrupted: still processing after the analysis timeout")
                    .SetProperty(batch => batch.UpdatedAt, now),
                cancellationToken);

        // A confirm or cancel holding the row lock makes this wait, and the status is checked again on the row it committed.
        var expired = await mine
            .Where(batch => !BulkAddRules.EndedStatuses.Contains(batch.Status) && batch.ExpiresAt <= now)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(batch => batch.Status, BulkBatchStatus.Expired)
                    .SetProperty(batch => batch.EndedAt, now)
                    .SetProperty(batch => batch.UpdatedAt, now),
                cancellationToken);

        await db.BulkPieces
            .Where(piece => (userId == null || piece.UserId == userId)
                && db.BulkBatches.Any(batch => batch.Id == piece.BatchId && BulkAddRules.EndedStatuses.Contains(batch.Status)))
            .ExecuteDeleteAsync(cancellationToken);

        var emptied = await EmptyEndedAsync(userId, now, cancellationToken);
        var orphans = await DeleteOrphansAsync(userId, cancellationToken);

        var tombstoneBefore = now - settings.TombstoneRetention;
        var tombstones = await mine
            .Where(batch => BulkAddRules.EndedStatuses.Contains(batch.Status) && batch.EndedAt <= tombstoneBefore && batch.ObjectsDeletedAt != null)
            .ExecuteDeleteAsync(cancellationToken);

        return new BulkCleanupSummary(interrupted, expired, emptied, orphans, tombstones);
    }

    private async Task<int> EmptyEndedAsync(Guid? userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var emptied = 0;
        var tried = new HashSet<Guid>();

        while (true)
        {
            var due = await db.BulkBatches.AsNoTracking()
                .Where(batch => (userId == null || batch.UserId == userId)
                    && BulkAddRules.EndedStatuses.Contains(batch.Status)
                    && batch.ObjectsDeletedAt == null
                    && !tried.Contains(batch.Id))
                .OrderBy(batch => batch.Id)
                .Select(batch => new { batch.Id, batch.UserId })
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
            {
                return emptied;
            }

            foreach (var batch in due)
            {
                tried.Add(batch.Id);
                if (await BulkStaging.DeleteObjectsAsync(db, storage, batch.UserId, batch.Id, now, logger, cancellationToken))
                {
                    emptied++;
                }
            }
        }
    }

    /// <summary>Objects under <c>bulk/</c> whose batch has no row any more, or has ended: only the prefix finds them.</summary>
    private async Task<int> DeleteOrphansAsync(Guid? userId, CancellationToken cancellationToken)
    {
        var prefix = userId is { } user ? StorageKeys.BulkUserPrefix(user) : $"{StorageKeys.BulkArea}/";
        var byBatch = new Dictionary<Guid, List<string>>();
        List<string> unreadable = [];

        try
        {
            await foreach (var stored in storage.ListAsync(prefix, cancellationToken))
            {
                if (BatchOf(stored.Key) is { } batchId)
                {
                    (byBatch.TryGetValue(batchId, out var keys) ? keys : byBatch[batchId] = []).Add(stored.Key);
                }
                else
                {
                    unreadable.Add(stored.Key);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSweepFailed(logger, exception);
            return 0;
        }

        if (byBatch.Count == 0 && unreadable.Count == 0)
        {
            return 0;
        }

        var ids = byBatch.Keys.ToList();
        var live = await db.BulkBatches.AsNoTracking()
            .Where(batch => ids.Contains(batch.Id) && !BulkAddRules.EndedStatuses.Contains(batch.Status))
            .Select(batch => batch.Id)
            .ToListAsync(cancellationToken);

        List<string> orphans = [.. unreadable, .. byBatch.Where(entry => !live.Contains(entry.Key)).SelectMany(entry => entry.Value)];
        if (orphans.Count == 0)
        {
            return 0;
        }

        try
        {
            await storage.DeleteAsync(orphans, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSweepFailed(logger, exception);
            return 0;
        }

        return orphans.Count;
    }

    /// <summary>The batch of a key <c>bulk/{user}/{batch}/...</c> (<see cref="StorageKeys.BuildBulk"/>), or null for a key of another shape.</summary>
    private static Guid? BatchOf(string key)
    {
        var parts = key.Split('/');

        return parts.Length >= 4 && Guid.TryParse(parts[2], out var batchId) ? batchId : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "bulk-add-cleanup: {Interrupted} interrupted, {Expired} expired, {Emptied} batch(es) emptied, {Orphans} orphaned object(s), {Tombstones} tombstone(s) deleted.")]
    private static partial void LogRun(ILogger logger, int interrupted, int expired, int emptied, int orphans, int tombstones);

    [LoggerMessage(Level = LogLevel.Warning, Message = "bulk-add-cleanup: sweeping orphaned objects failed; the next run tries again.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
