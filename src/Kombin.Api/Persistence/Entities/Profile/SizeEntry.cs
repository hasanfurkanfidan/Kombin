namespace Kombin.Api.Persistence.Entities.Profile;

/// <summary>The size a user wears at a brand, per garment group. Unique per (user, brand, group).</summary>
internal sealed class SizeEntry : BaseEntity
{
    public Guid UserId { get; set; }

    public Guid BrandId { get; set; }

    public SizeGroup Group { get; set; }

    /// <summary>Trimmed and upper-cased.</summary>
    public string Size { get; set; } = string.Empty;

    public SizeSource Source { get; set; }

    /// <summary>1.0 for entered and confirmed sizes; lowered when a purchase turned out not to fit.</summary>
    public decimal Confidence { get; set; } = 1m;
}
