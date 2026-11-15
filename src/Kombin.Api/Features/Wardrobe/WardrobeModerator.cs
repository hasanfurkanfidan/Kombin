using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe;

/// <summary>
/// The safety filter for wardrobe photos (security review M3). A wardrobe item's image can reach other users through a post's wardrobe tag, so
/// its cutout goes through <c>kombin-vision /v1/moderate</c> like a post photo. The verdict never blocks the owner: every item stays usable in
/// its owner's wardrobe; only <see cref="IsShareable"/> items show their image to anyone else. Runs right after <c>wardrobe-analyze</c> made the
/// item <c>Ready</c> (so the 3-second rule is untouched); a failed call leaves the verdict empty (not shareable) and <c>wardrobe-cleanup</c>
/// asks again (<see cref="RetryMissingAsync"/>).
/// </summary>
internal sealed partial class WardrobeModerator(KombinDbContext db, IObjectStorage storage, IVisionClient vision, IClock clock, ILogger<WardrobeModerator> logger)
{
    /// <summary>Items <c>wardrobe-cleanup</c> retries per run.</summary>
    public const int RetryBatch = 200;

    /// <summary>Whether another user may see the item's image: only an <c>Allow</c> verdict. Not yet moderated, review and block all keep it private.</summary>
    public static bool IsShareable(WardrobeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.ModerationVerdict == ModerationVerdict.Allow;
    }

    /// <summary>
    /// Moderates the item's cutout (<paramref name="cutout"/> when the caller already holds it, else read from storage) and saves the verdict.
    /// Null when the service could not answer; the item is left as it was.
    /// </summary>
    public async Task<ModerationVerdict?> ModerateAsync(WardrobeItem item, byte[]? cutout, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (cutout is null)
        {
            var key = item.ImageCutoutKey ?? item.ImageThumbKey;
            if (key is null || await storage.GetAsync(key, cancellationToken) is not { } stored)
            {
                return null;
            }

            cutout = stored.Content;
            contentType = stored.ContentType;
        }

        var moderation = await vision.ModerateAsync(cutout, contentType, renditions: false, cancellationToken);
        if (moderation.Value is not { } answer)
        {
            LogUnanswered(logger, item.Id, moderation.Error ?? "vision_failed");
            return null;
        }

        item.ModerationVerdict = ParseVerdict(answer.Verdict);
        item.ModeratedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return item.ModerationVerdict;
    }

    /// <summary>Ready items with an image but no verdict yet (the call failed), oldest first; <paramref name="userId"/> scopes it for tests.</summary>
    public async Task<int> RetryMissingAsync(Guid? userId, CancellationToken cancellationToken)
    {
        var due = db.WardrobeItems.Where(item =>
            item.Status == WardrobeItemStatus.Ready && !item.IsArchived && item.ModerationVerdict == null && (item.ImageCutoutKey != null || item.ImageThumbKey != null));

        if (userId is { } onlyUser)
        {
            due = due.Where(item => item.UserId == onlyUser);
        }

        var batch = await due.OrderBy(item => item.UpdatedAt).ThenBy(item => item.Id).Take(RetryBatch).ToListAsync(cancellationToken);
        var answered = 0;

        foreach (var item in batch)
        {
            if (await ModerateAsync(item, null, string.Empty, cancellationToken) is not null)
            {
                answered++;
            }
        }

        return answered;
    }

    /// <summary>Like a post's: an answer the filter does not know counts as review.</summary>
    private static ModerationVerdict ParseVerdict(string? verdict) => verdict?.ToLowerInvariant() switch
    {
        "allow" => ModerationVerdict.Allow,
        "block" => ModerationVerdict.Block,
        _ => ModerationVerdict.Review,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wardrobe item {ItemId}: moderation did not answer ({Reason}); its image stays private until a retry.")]
    private static partial void LogUnanswered(ILogger logger, Guid itemId, string reason);
}
