using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Identity;

internal enum PurgeStatus
{
    /// <summary>Objects and rows are gone.</summary>
    Purged,

    /// <summary>There is no such user (already purged).</summary>
    NotFound,

    /// <summary>The storage refused the delete; nothing else was touched, so the next run tries again.</summary>
    StorageFailed,
}

internal readonly record struct PurgeResult(PurgeStatus Status, int ObjectsDeleted);

/// <summary>
/// The second half of account deletion: removes one user and everything of theirs. Used by the <c>account-deletion</c> job when the window is
/// over and by the admin panel's "Delete now". The order is what makes it resumable: the stored objects go first (their keys are read from
/// the rows, which are still there if anything fails), then, in one transaction, the counters and references that must survive are fixed and
/// the user row is deleted. The foreign keys then remove the rest (see <c>docs/privacy-notes.md</c>, section 1a, for every table).
/// </summary>
internal sealed partial class AccountPurger(KombinDbContext db, IObjectStorage storage, ILogger<AccountPurger> logger)
{
    public async Task<PurgeResult> PurgeAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return new PurgeResult(PurgeStatus.NotFound, 0);
        }

        var keys = await ObjectKeysAsync(userId, cancellationToken);

        try
        {
            await storage.DeleteAsync(keys, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailed(logger, exception, userId, keys.Count);
            return new PurgeResult(PurgeStatus.StorageFailed, 0);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Likes this user gave to other people's posts: the posts keep a counter, so take their like back (own posts are deleted below).
        await db.Posts
            .Where(post => post.LikeCount > 0 && db.PostLikes.Any(like => like.PostId == post.Id && like.UserId == userId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(post => post.LikeCount, post => post.LikeCount - 1), cancellationToken);

        // Anonymised, not deleted: clicks and analytics events are the revenue and product record. The persona is a user-specific ID too.
        await db.Clicks
            .Where(click => click.UserId == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(click => click.UserId, (Guid?)null).SetProperty(click => click.PersonaId, (Guid?)null),
                cancellationToken);
        await db.Clicks
            .Where(click => click.CreatorId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(click => click.CreatorId, (Guid?)null), cancellationToken);
        await db.AnalyticsEvents
            .Where(analyticsEvent => analyticsEvent.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(analyticsEvent => analyticsEvent.UserId, (Guid?)null), cancellationToken);

        // Referral rows keep their amounts; only the people are cut loose.
        await db.ReferralEvents
            .Where(referral => referral.CreatorId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(referral => referral.CreatorId, (Guid?)null), cancellationToken);
        await db.ReferralEvents
            .Where(referral => referral.ViewerUserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(referral => referral.ViewerUserId, (Guid?)null), cancellationToken);

        // The cascade: external logins, refresh tokens, profile, personas (+ swipes, style profiles), sizes, saved products, alerts (+ triggers),
        // notifications, devices, preferences, wardrobe items (+ wears), outfits (+ items, feedback), posts (+ tags, likes, reports), follows,
        // blocks, likes, reports and posting consents of the user, and the creator profile.
        await db.Users.Where(user => user.Id == userId).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return new PurgeResult(PurgeStatus.Purged, keys.Count);
    }

    /// <summary>Every object of the user: wardrobe originals, cutouts and thumbnails, post originals, feed images and previews.</summary>
    private async Task<List<string>> ObjectKeysAsync(Guid userId, CancellationToken cancellationToken)
    {
        var wardrobe = await db.WardrobeItems
            .Where(item => item.UserId == userId)
            .Select(item => new { item.ImageOriginalKey, item.ImageCutoutKey, item.ImageThumbKey })
            .ToListAsync(cancellationToken);

        var posts = await db.Posts
            .Where(post => post.AuthorId == userId)
            .Select(post => new { post.ImageOriginalKey, post.ImageFeedKey, post.ImagePreviewKey })
            .ToListAsync(cancellationToken);

        return
        [
            .. wardrobe.SelectMany(item => new[] { item.ImageOriginalKey, item.ImageCutoutKey, item.ImageThumbKey }.OfType<string>()),
            .. posts.SelectMany(post => new[] { post.ImageOriginalKey, post.ImageFeedKey, post.ImagePreviewKey }.OfType<string>()),
        ];
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Deleting the stored objects of user {UserId} ({Objects} object(s)) failed; the account stays and the next run tries again.")]
    private static partial void LogStorageFailed(ILogger logger, Exception exception, Guid userId, int objects);
}
