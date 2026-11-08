using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.Jobs;

internal enum PostProcessOutcome
{
    /// <summary>The post is gone or no longer processing; nothing was done.</summary>
    Skipped,
    Draft,
    NeedsReview,
    Blocked,
    Failed,

    /// <summary>The vision service could not be reached; the attempt is counted and Hangfire tries again.</summary>
    Retry,
}

/// <summary>
/// Makes an uploaded post safe to show, then proposes tags. Photo: original → metadata stripped (written back, so the private original keeps
/// no location either) → <c>/v1/moderate?renditions=true</c> → feed image and preview stored (private) → <c>/v1/detect</c> → up to 6 unconfirmed
/// proposals. Outfit: each wardrobe piece's cutout goes through <c>/v1/moderate</c> (catalog images are trusted); the strictest verdict wins.
/// The verdict sets the status (<see cref="PostRules.StatusAfterModeration"/>). A post is never published by this job: it ends as a draft
/// for the author to confirm. Fire-and-forget, enqueued by <c>process</c> and <c>from-outfit</c>; idempotent (it only works on a
/// <c>Processing</c> post and overwrites the same keys and proposals). Unreachable vision: retried, the <see cref="MaxAttempts"/>th failure
/// makes the post <c>Failed</c>; it is never published unmoderated. Detection is assistance: when it fails the post simply has no proposals.
/// </summary>
internal sealed partial class PostProcessJob(KombinDbContext db, IObjectStorage storage, IVisionClient vision, IClock clock, ILogger<PostProcessJob> logger)
{
    public const string JobId = "post-process";

    /// <summary>The first try plus Hangfire's three retries.</summary>
    public const int MaxAttempts = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [AutomaticRetry(Attempts = MaxAttempts - 1, DelaysInSeconds = [5, 15, 30], OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [JobDisplayName("post-process {0}")]
    public async Task RunAsync(Guid postId, CancellationToken cancellationToken)
    {
        if (await ProcessAsync(postId, cancellationToken) == PostProcessOutcome.Retry)
        {
            throw new InvalidOperationException($"The vision service was not reachable for post {postId}; the job will be retried.");
        }
    }

    public async Task<PostProcessOutcome> ProcessAsync(Guid postId, CancellationToken cancellationToken)
    {
        var post = await db.Posts.Include(candidate => candidate.Tags).SingleOrDefaultAsync(candidate => candidate.Id == postId, cancellationToken);
        if (post is not { Status: PostStatus.Processing })
        {
            return PostProcessOutcome.Skipped;
        }

        return post.Kind == PostKind.Photo
            ? await ProcessPhotoAsync(post, cancellationToken)
            : await ProcessOutfitAsync(post, cancellationToken);
    }

    private async Task<PostProcessOutcome> ProcessPhotoAsync(Post post, CancellationToken cancellationToken)
    {
        if (post.ImageOriginalKey is null || await storage.GetAsync(post.ImageOriginalKey, cancellationToken) is not { } original)
        {
            return await FailAsync(post, "original_missing", cancellationToken);
        }

        if (original.Content.LongLength > SocialLimits.MaxImageBytes)
        {
            return await FailAsync(post, "image_too_large", cancellationToken);
        }

        // Metadata goes before anything else sees the photo, and the stored original is replaced by the stripped one.
        if (ImageMetadata.Strip(original.Content) is not { } stripped)
        {
            return await FailAsync(post, "unsupported_image: not a JPEG, PNG or WEBP", cancellationToken);
        }

        if (stripped.RemovedSegments > 0 || stripped.ContentType != original.ContentType)
        {
            await storage.PutAsync(post.ImageOriginalKey, stripped.Content, stripped.ContentType, cancellationToken);
        }

        var moderation = await vision.ModerateAsync(stripped.Content, stripped.ContentType, renditions: true, cancellationToken);
        if (moderation.Value is not { } answer)
        {
            return await CallFailedAsync(post, moderation.Error, moderation.IsTransient, cancellationToken);
        }

        var verdict = ParseVerdict(answer.Verdict);
        VisionDetection? detection = null;

        if (verdict != ModerationVerdict.Block)
        {
            if (answer.Renditions is not { } renditions)
            {
                return await FailAsync(post, "vision_bad_response: no renditions", cancellationToken);
            }

            byte[] feed;
            byte[] preview;
            try
            {
                feed = Convert.FromBase64String(renditions.FeedImage);
                preview = Convert.FromBase64String(renditions.Preview);
            }
            catch (FormatException)
            {
                return await FailAsync(post, "vision_bad_response: images are not base64", cancellationToken);
            }

            // Belt and braces: the renditions are re-encoded without metadata, but nothing is stored unchecked.
            feed = ImageMetadata.Strip(feed)?.Content ?? feed;
            preview = ImageMetadata.Strip(preview)?.Content ?? preview;

            var feedKey = StorageKeys.Build(SocialLimits.StorageArea, post.AuthorId, post.Id, ImageVariant.Feed, "jpg");
            var previewKey = StorageKeys.Build(SocialLimits.StorageArea, post.AuthorId, post.Id, ImageVariant.Preview, "webp");
            await storage.PutAsync(feedKey, feed, "image/jpeg", cancellationToken);
            await storage.PutAsync(previewKey, preview, "image/webp", cancellationToken);

            post.ImageFeedKey = feedKey;
            post.ImagePreviewKey = previewKey;
            post.ImageAspect = renditions.FeedHeight > 0 ? Math.Round(renditions.FeedWidth / (double)renditions.FeedHeight, 4) : null;

            var detected = await vision.DetectAsync(stripped.Content, stripped.ContentType, embed: false, cancellationToken);
            detection = detected.Value;
            if (detection is null)
            {
                LogDetectionFailed(logger, post.Id, detected.Error ?? "unknown");
            }
            else
            {
                await ReplaceProposalsAsync(post, detection, cancellationToken);
            }
        }

        return await DecideAsync(post, verdict, JsonSerializer.Serialize(new { photo = Summary(answer), layout = detection?.Layout }, Json), cancellationToken);
    }

    private async Task<PostProcessOutcome> ProcessOutfitAsync(Post post, CancellationToken cancellationToken)
    {
        var itemIds = post.Tags.Select(tag => tag.WardrobeItemId).OfType<Guid>().ToList();
        var images = await db.WardrobeItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .Select(item => new { item.Id, Key = item.ImageCutoutKey ?? item.ImageThumbKey })
            .ToListAsync(cancellationToken);

        var verdicts = new List<ModerationVerdict>();
        var summaries = new Dictionary<Guid, object>();
        foreach (var image in images.Where(image => image.Key is not null))
        {
            if (await storage.GetAsync(image.Key!, cancellationToken) is not { } stored)
            {
                continue;
            }

            var moderation = await vision.ModerateAsync(stored.Content, stored.ContentType, renditions: false, cancellationToken);
            if (moderation.Value is not { } answer)
            {
                return await CallFailedAsync(post, moderation.Error, moderation.IsTransient, cancellationToken);
            }

            verdicts.Add(ParseVerdict(answer.Verdict));
            summaries[image.Id] = Summary(answer);
        }

        return await DecideAsync(post, PostRules.Combine(verdicts), JsonSerializer.Serialize(new { pieces = summaries }, Json), cancellationToken);
    }

    private async Task<PostProcessOutcome> DecideAsync(Post post, ModerationVerdict verdict, string moderationJson, CancellationToken cancellationToken)
    {
        post.ModerationVerdict = verdict;
        post.ModerationJson = moderationJson;
        post.Status = PostRules.StatusAfterModeration(verdict);
        post.ModerationReason = verdict switch
        {
            ModerationVerdict.Block => SocialCopy.Blocked,
            ModerationVerdict.Review => SocialCopy.InReview,
            _ => null,
        };
        post.FailureReason = null;

        await db.SaveChangesAsync(cancellationToken);
        LogDecided(logger, post.Id, post.Status);

        return post.Status switch
        {
            PostStatus.Draft => PostProcessOutcome.Draft,
            PostStatus.NeedsReview => PostProcessOutcome.NeedsReview,
            _ => PostProcessOutcome.Blocked,
        };
    }

    /// <summary>The detector's regions become unconfirmed proposals (earlier detected proposals are replaced, so a rerun does not pile up).</summary>
    private async Task ReplaceProposalsAsync(Post post, VisionDetection detection, CancellationToken cancellationToken)
    {
        var slugs = detection.Regions.Select(region => region.Category.Slug).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking()
            .Where(category => slugs.Contains(category.Slug))
            .ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);

        var stale = post.Tags.Where(tag => tag.ConfirmedAt is null && tag.Source == PostTagSource.Detected).ToList();
        db.PostTags.RemoveRange(stale);
        stale.ForEach(tag => post.Tags.Remove(tag));

        var now = clock.UtcNow;
        var position = post.Tags.Count;
        foreach (var region in detection.Regions.Take(SocialLimits.MaxTags))
        {
            var (x, y, w, h) = TagBoxes.Clamp(region.Box.X, region.Box.Y, region.Box.W, region.Box.H);
            post.Tags.Add(new PostTag
            {
                PostId = post.Id,
                Position = position++,
                X = x,
                Y = y,
                W = w,
                H = h,
                Kind = PostTagKind.Unknown,
                Source = PostTagSource.Detected,
                CategoryId = categories.TryGetValue(region.Category.Slug, out var categoryId) ? categoryId : null,
                Color = EnumNames.TryParse<ProductColor>(region.Color.Name, out var color) ? color : null,
                Confidence = Math.Round(region.Confidence, 4),
                CreatedAt = now,
            });
        }
    }

    private async Task<PostProcessOutcome> CallFailedAsync(Post post, string? error, bool transient, CancellationToken cancellationToken)
    {
        var reason = error ?? "vision_failed";
        if (!transient)
        {
            return await FailAsync(post, reason, cancellationToken);
        }

        post.ProcessingAttempts++;
        if (post.ProcessingAttempts >= MaxAttempts)
        {
            return await FailAsync(post, reason, cancellationToken);
        }

        post.FailureReason = Clip(reason);
        await db.SaveChangesAsync(cancellationToken);
        LogRetry(logger, post.Id, post.ProcessingAttempts, reason);

        return PostProcessOutcome.Retry;
    }

    private async Task<PostProcessOutcome> FailAsync(Post post, string reason, CancellationToken cancellationToken)
    {
        post.Status = PostStatus.Failed;
        post.FailureReason = Clip(reason);
        post.ModerationReason = SocialCopy.ProcessingFailed;
        await db.SaveChangesAsync(cancellationToken);
        LogFailed(logger, post.Id, reason);

        return PostProcessOutcome.Failed;
    }

    /// <summary>An unknown verdict is treated as a review: never publish what we could not read.</summary>
    private static ModerationVerdict ParseVerdict(string? verdict) => verdict?.ToLowerInvariant() switch
    {
        "allow" => ModerationVerdict.Allow,
        "block" => ModerationVerdict.Block,
        _ => ModerationVerdict.Review,
    };

    private static object Summary(VisionModeration answer) => new { answer.Verdict, answer.Labels, answer.Scores };

    private static string Clip(string reason) => reason.Length <= 300 ? reason : reason[..300];

    [LoggerMessage(Level = LogLevel.Information, Message = "Post {PostId} processed: {Status}.")]
    private static partial void LogDecided(ILogger logger, Guid postId, PostStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Post {PostId}: garment detection failed ({Reason}); no proposals.")]
    private static partial void LogDetectionFailed(ILogger logger, Guid postId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Post {PostId}: vision attempt {Attempt} failed ({Reason}); retrying.")]
    private static partial void LogRetry(ILogger logger, Guid postId, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Post {PostId} failed: {Reason}.")]
    private static partial void LogFailed(ILogger logger, Guid postId, string reason);
}
