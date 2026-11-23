using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Persistence.Entities.AskFriend;

/// <summary>
/// One "Arkadaşına sor" question: one outfit (Yes/No) or two (A/B), shared as a public link for 24 hours. Design: <c>docs/design/ask-a-friend.md</c>.
/// </summary>
internal sealed class Ask
{
    /// <summary>16 random bytes as base64url without padding.</summary>
    public const int TokenLength = 22;

    public const int MaxConsentVersionLength = 40;

    /// <summary>Time-ordered GUID (v7) so inserts stay index-friendly.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    /// <summary>The link token, stored as is (it opens one page and a vote, never an account).</summary>
    public string Token { get; set; } = string.Empty;

    public AskKind Kind { get; set; }

    public AskStatus Status { get; set; }

    /// <summary>The consent version the asker had accepted when the ask was made.</summary>
    public string ConsentVersion { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the asker closed it (only with <see cref="AskStatus.Closed"/>).</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>When the public image copies were deleted; null while they may still exist.</summary>
    public DateTimeOffset? CopiesDeletedAt { get; set; }

    public int PageViews { get; set; }

    public int StoreClicks { get; set; }

    public int PushCount { get; set; }

    public List<AskPiece> Pieces { get; } = [];
}

/// <summary>One shown slot of an option, built at creation.</summary>
internal sealed class AskPiece
{
    public const int MaxCopyKeyLength = 300;

    public const int MaxImageUrlLength = 2048;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AskId { get; set; }

    /// <summary>A <see cref="AskKind.Single"/> ask has only <see cref="AskOption.A"/>.</summary>
    public AskOption Option { get; set; }

    /// <summary>The <c>recommendation.outfits</c> row it came from; null once that row is gone.</summary>
    public Guid? OutfitId { get; set; }

    public OutfitSlot Slot { get; set; }

    public short Position { get; set; }

    public Guid? WardrobeItemId { get; set; }

    public Guid? ProductId { get; set; }

    /// <summary>The ask's own public copy of a wardrobe thumbnail; null when there is none.</summary>
    public string? CopyKey { get; set; }

    /// <summary>A catalog image URL, else null.</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>One browser's answer to an ask (replaced, not added, when the same browser answers again).</summary>
internal sealed class AskAnswer : BaseEntity
{
    /// <summary>SHA-256 hex of the browser cookie.</summary>
    public const int VoterHashLength = 64;

    public const int MaxNameLength = 24;

    public const int MaxNoteLength = 140;

    public Guid AskId { get; set; }

    public string VoterHash { get; set; } = string.Empty;

    public AskChoice Choice { get; set; }

    public string? Name { get; set; }

    public string? Note { get; set; }

    /// <summary>When the asker was told about it; null while waiting for the push.</summary>
    public DateTimeOffset? NotifiedAt { get; set; }

    /// <summary>When the asker hid it; a hidden answer is not counted or shown.</summary>
    public DateTimeOffset? HiddenAt { get; set; }
}

/// <summary>One browser's report of an ask.</summary>
internal sealed class AskReport
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AskId { get; set; }

    /// <summary>SHA-256 hex of the browser cookie (<see cref="AskAnswer.VoterHashLength"/>).</summary>
    public string VoterHash { get; set; } = string.Empty;

    public AskReportReason Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only proof that a user accepted a version of the ask consent, with the exact text shown.</summary>
internal sealed class AskConsentRecord
{
    public const int MaxVersionLength = 40;

    public const int MaxTextLength = 4000;

    public Guid UserId { get; set; }

    public string Version { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset AcceptedAt { get; set; }
}
