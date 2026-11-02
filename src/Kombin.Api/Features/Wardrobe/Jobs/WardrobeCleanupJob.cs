using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.Jobs;

internal readonly record struct CleanupSummary(int ArchivedItemsCleaned, int AbandonedUploadsDeleted);

/// <summary>
/// Daily: deletes the stored images of items archived more than <see cref="Retention"/> ago (the row stays, without image keys, so wear
/// history survives), and deletes uploads that were started but never processed within <see cref="AbandonedAfter"/>, row and object.
/// Idempotent: cleaned items no longer match.
/// </summary>
internal sealed partial class WardrobeCleanupJob(KombinDbContext db, IObjectStorage storage, IClock clock, ILogger<WardrobeCleanupJob> logger)
{
    public const string JobId = "wardrobe-cleanup";

    /// <summary>Daily at 05:00 in Europe/Istanbul (the zone is set where the job is registered).</summary>
    public const string Cron = "0 5 * * *";

    public const int BatchSize = 200;

    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(1);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await CleanupAsync(null, cancellationToken);

        LogRun(logger, summary.ArchivedItemsCleaned, summary.AbandonedUploadsDeleted);
    }

    /// <summary><paramref name="userId"/> restricts the run to one user's items (tests share a database).</summary>
    public async Task<CleanupSummary> CleanupAsync(Guid? userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        return new CleanupSummary(
            await CleanArchivedAsync(userId, now - Retention, cancellationToken),
            await DeleteAbandonedAsync(userId, now - AbandonedAfter, cancellationToken));
    }

    private async Task<int> CleanArchivedAsync(Guid? userId, DateTimeOffset archivedBefore, CancellationToken cancellationToken)
    {
        var cleaned = 0;

        while (true)
        {
            var due = db.WardrobeItems.Where(item =>
                item.IsArchived
                && item.ArchivedAt < archivedBefore
                && (item.ImageOriginalKey != null || item.ImageCutoutKey != null || item.ImageThumbKey != null));

            if (userId is { } onlyUser)
            {
                due = due.Where(item => item.UserId == onlyUser);
            }

            // Cleaned items drop out of the filter, so every pass makes progress.
            var batch = await due.OrderBy(item => item.ArchivedAt).ThenBy(item => item.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return cleaned;
            }

            // Objects first: if the delete fails the keys are still on the rows and the next run tries again.
            await storage.DeleteAsync([.. batch.SelectMany(KeysOf)], cancellationToken);

            foreach (var item in batch)
            {
                item.ImageOriginalKey = null;
                item.ImageCutoutKey = null;
                item.ImageThumbKey = null;
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            cleaned += batch.Count;
        }
    }

    private async Task<int> DeleteAbandonedAsync(Guid? userId, DateTimeOffset createdBefore, CancellationToken cancellationToken)
    {
        var deleted = 0;

        while (true)
        {
            var abandoned = db.WardrobeItems.Where(item =>
                item.Status == WardrobeItemStatus.Processing && item.ProcessRequestedAt == null && item.CreatedAt < createdBefore);

            if (userId is { } onlyUser)
            {
                abandoned = abandoned.Where(item => item.UserId == onlyUser);
            }

            var batch = await abandoned.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return deleted;
            }

            await storage.DeleteAsync([.. batch.SelectMany(KeysOf)], cancellationToken);
            db.WardrobeItems.RemoveRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            deleted += batch.Count;
        }
    }

    private static IEnumerable<string> KeysOf(WardrobeItem item) =>
        new[] { item.ImageOriginalKey, item.ImageCutoutKey, item.ImageThumbKey }.OfType<string>();

    [LoggerMessage(Level = LogLevel.Information, Message = "Wardrobe cleanup: images of {Archived} archived item(s) deleted, {Abandoned} abandoned upload(s) deleted.")]
    private static partial void LogRun(ILogger logger, int archived, int abandoned);
}
