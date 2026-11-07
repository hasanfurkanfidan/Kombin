namespace Kombin.Api.Persistence.Entities.Social;

/// <summary>A photo of someone in an outfit, or a saved outfit shown as a collage of its pieces (no person).</summary>
internal enum PostKind
{
    Photo,
    Outfit,
}

/// <summary>Who may see a live post. New posts are <see cref="Private"/> until the author chooses otherwise.</summary>
internal enum PostVisibility
{
    Private,
    Followers,
    Public,
}

/// <summary>
/// <c>Processing</c> → <c>Blocked</c> | <c>NeedsReview</c> | <c>Draft</c> (or <c>Failed</c> when the photo could not be processed); the
/// author's confirmation turns a <c>Draft</c> into <c>Live</c> (a <c>NeedsReview</c> post waits for a reviewer). Three reports hide a
/// <c>Live</c> post. <c>Deleted</c> is a tombstone until the purge job.
/// </summary>
internal enum PostStatus
{
    Processing,
    Draft,
    NeedsReview,
    Blocked,
    Live,
    Hidden,
    Deleted,
    Failed,
}

internal enum PostTagKind
{
    Wardrobe,
    Product,
    Unknown,
}

/// <summary>Where a tag came from. Whatever the source, a tag is shown to viewers only once the author confirmed it.</summary>
internal enum PostTagSource
{
    Detected,
    User,
    Outfit,
}

internal enum ReportReason
{
    Nudity,
    Violence,
    Hate,
    Harassment,
    Spam,
    Scam,
    Other,
}

/// <summary>What the safety filter said about a post's images.</summary>
internal enum ModerationVerdict
{
    Allow,
    Review,
    Block,
}

internal enum ReferralKind
{
    /// <summary>A buy click on a creator's tag (written by <c>/go</c>).</summary>
    Click,

    /// <summary>A purchase reported by an affiliate network for such a click (imported later; nothing writes it yet).</summary>
    Conversion,
}
