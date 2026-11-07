using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.Jobs;

internal readonly record struct PurgeSummary(int PostsPurged, int ObjectsDeleted);

/// <summary>
/// Hourly: hard-deletes what users deleted and what nobody will ever see (<see cref="PurgeRules"/>): the storage objects first (original,
/// feed image, preview), then the row with its tags, likes and reports. Running every hour is what makes "deleted within 24 h" true even
/// when a few runs fail. Clicks and referral events keep the post's ID (no foreign key): they are the revenue record and hold no image.
/// Idempotent: purged posts no longer exist.
/// </summary>
internal sealed partial class SocialPurgeJob(KombinDbContext db, IObjectStorage storage, IClock clock, ILogger<SocialPurgeJob> logger)
{
    public const string JobId = "social-purge";

    public const string Cron = "17 * * * *";

    public const int BatchSize = 200;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await PurgeAsync(null, cancellationToken);

        LogRun(logger, summary.PostsPurged, summary.ObjectsDeleted);
    }

    /// <summary><paramref name="authorId"/> restricts the run to one author's posts (tests share a database).</summary>
    public async Task<PurgeSummary> PurgeAsync(Guid? authorId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var abandoned = now - PurgeRules.AbandonedUpload;
        var staleDraft = now - PurgeRules.StaleDraft;
        var blockedBefore = now - PurgeRules.BlockedRetention;
        var posts = 0;
        var objects = 0;

        while (true)
        {
            var due = db.Posts.Where(post =>
                post.Status == PostStatus.Deleted
                || (post.Status == PostStatus.Processing && post.ProcessRequestedAt == null && post.CreatedAt < abandoned)
                || (post.Status == PostStatus.Failed && post.UpdatedAt < abandoned)
                || (post.Status == PostStatus.Draft && post.UpdatedAt < staleDraft)
                || (post.Status == PostStatus.Blocked && post.UpdatedAt < blockedBefore));

            if (authorId is { } onlyAuthor)
            {
                due = due.Where(post => post.AuthorId == onlyAuthor);
            }

            var batch = await due.OrderBy(post => post.UpdatedAt).ThenBy(post => post.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return new PurgeSummary(posts, objects);
            }

            // Objects first: if the delete fails the rows (and their keys) are still there for the next run.
            List<string> keys = [.. batch.SelectMany(KeysOf)];
            await storage.DeleteAsync(keys, cancellationToken);

            db.Posts.RemoveRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            posts += batch.Count;
            objects += keys.Count;
        }
    }

    private static IEnumerable<string> KeysOf(Post post) =>
        new[] { post.ImageOriginalKey, post.ImageFeedKey, post.ImagePreviewKey }.OfType<string>();

    [LoggerMessage(Level = LogLevel.Information, Message = "Social purge: {Posts} post(s) and {Objects} stored object(s) deleted.")]
    private static partial void LogRun(ILogger logger, int posts, int objects);
}
