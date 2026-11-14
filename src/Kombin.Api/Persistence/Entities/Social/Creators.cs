namespace Kombin.Api.Persistence.Entities.Social;

/// <summary>
/// A creator: a user the founder invited (set through the internal endpoint, no self-signup in v1). Their posts carry their referral:
/// a buy click on one of their tags is attributed to them. <see cref="IsActive"/> false keeps the handle but stops attribution.
/// </summary>
internal sealed class CreatorProfile : IAuditable
{
    public const int MaxHandleLength = 30;

    public const int MaxBioLength = 160;

    public const int MaxLinkLength = 200;

    public Guid UserId { get; set; }

    /// <summary>Lower-case, 3-30 of a-z, 0-9, <c>.</c> and <c>_</c>; unique.</summary>
    public string Handle { get; set; } = string.Empty;

    public string? Bio { get; set; }

    public string? Link { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Append-only creator attribution. <c>/go</c> writes a <see cref="ReferralKind.Click"/> row for each buy click on a creator's tag. When
/// affiliate conversions are imported (not built yet), each reported order is matched by our click id (the <c>kombin_click</c> parameter the
/// redirect adds) and written as a <see cref="ReferralKind.Conversion"/> row with the amounts: <c>CreatorAmount = CommissionAmount ×
/// SharePercent / 100</c>, the share being the one in force when the click happened.
/// </summary>
internal sealed class ReferralEvent
{
    public long Id { get; set; }

    public ReferralKind Kind { get; set; }

    /// <summary>The creator who earns the share; null once their account is deleted (the amounts stay as the financial record).</summary>
    public Guid? CreatorId { get; set; }

    /// <summary>No foreign key: a deleted post must not take the creator's earnings with it.</summary>
    public Guid PostId { get; set; }

    public Guid? PostTagId { get; set; }

    public long? ClickId { get; set; }

    public Guid? ProductId { get; set; }

    public Guid? OfferId { get; set; }

    public Guid? SellerId { get; set; }

    /// <summary>The signed-in clicker, null when anonymous.</summary>
    public Guid? ViewerUserId { get; set; }

    /// <summary><c>Creators:SharePercent</c> when the click happened.</summary>
    public decimal SharePercent { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public decimal? OrderAmount { get; set; }

    public decimal? CommissionAmount { get; set; }

    public decimal? CreatorAmount { get; set; }

    public string Currency { get; set; } = "TRY";

    /// <summary>The affiliate network's order reference (conversions only), unique per seller.</summary>
    public string? ExternalOrderRef { get; set; }

    public DateTimeOffset? ImportedAt { get; set; }
}
