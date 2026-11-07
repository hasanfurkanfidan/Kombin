using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Persistence.Entities.Social;

/// <summary>
/// An outfit post. A photo post has a private original and two private renditions made by the vision service (a feed image of at most
/// 1600 px and a 400 px preview, both re-encoded without metadata); an outfit post has no image of its own, its tags carry the pieces'
/// images. Visible to others only while <see cref="PostStatus.Live"/>, and then only as <see cref="Visibility"/> allows.
/// </summary>
internal sealed class Post : BaseEntity
{
    public const int MaxCaptionLength = 300;

    public const int MaxModerationReasonLength = 200;

    public Guid AuthorId { get; set; }

    public PostKind Kind { get; set; }

    public PostVisibility Visibility { get; set; } = PostVisibility.Private;

    public PostStatus Status { get; set; } = PostStatus.Processing;

    /// <summary>The author's audience when the post was created (the for-you feed matches it against the viewer's).</summary>
    public Audience Audience { get; set; } = Audience.Unisex;

    public string? Caption { get; set; }

    /// <summary>The posting consent the author had accepted when creating the post (<c>PostingConsent</c> version).</summary>
    public string ConsentVersion { get; set; } = string.Empty;

    public DateTimeOffset ConsentAt { get; set; }

    public ModerationVerdict? ModerationVerdict { get; set; }

    /// <summary>The vision service's answer without images: verdict, labels and scores per image (jsonb).</summary>
    public string? ModerationJson { get; set; }

    /// <summary>What the author is told when the post is blocked (Turkish, from <c>SocialCopy</c>).</summary>
    public string? ModerationReason { get; set; }

    /// <summary>The upload's content type (image/jpeg, image/png or image/webp).</summary>
    public string? ContentType { get; set; }

    public string? ImageOriginalKey { get; set; }

    public string? ImageFeedKey { get; set; }

    public string? ImagePreviewKey { get; set; }

    /// <summary>Width / height of the feed image, so the app can lay out the card before the image loads.</summary>
    public double? ImageAspect { get; set; }

    /// <summary>The saved outfit an outfit post was made from (no foreign key: the outfit may be dismissed later).</summary>
    public Guid? OutfitId { get; set; }

    public int LikeCount { get; set; }

    /// <summary>Distinct reporters.</summary>
    public int ReportCount { get; set; }

    public DateTimeOffset? ProcessRequestedAt { get; set; }

    /// <summary>Failed calls to the vision service while processing (the job's own count, like the wardrobe analysis).</summary>
    public int ProcessingAttempts { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>When the author confirmed the tags, caption and visibility.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>When the post first went live (the feed's recency).</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? HiddenAt { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public List<PostTag> Tags { get; } = [];
}
