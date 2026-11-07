namespace Kombin.Api.Persistence.Entities.Social;

/// <summary><see cref="FollowerId"/> follows <see cref="FolloweeId"/>. Open follow in v1: no request to approve.</summary>
internal sealed class Follow
{
    public Guid FollowerId { get; set; }

    public Guid FolloweeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// <see cref="BlockerId"/> blocked <see cref="BlockedId"/>: neither sees the other's posts or profile, follows between them are removed and
/// cannot be made again. The blocked user is not told.
/// </summary>
internal sealed class UserBlock
{
    public Guid BlockerId { get; set; }

    public Guid BlockedId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class PostLike
{
    public Guid PostId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One report per user and post. The reporter is never shown to the author.</summary>
internal sealed class PostReport
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid PostId { get; set; }

    public Guid ReporterId { get; set; }

    public ReportReason Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Append-only proof that a user accepted the posting consent: the version and the exact text that was shown, with the time. A new
/// text is a new version, which every user has to accept again before posting.
/// </summary>
internal sealed class PostingConsentRecord
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public string Version { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset AcceptedAt { get; set; }
}
