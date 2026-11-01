namespace Kombin.Api.Persistence.Entities.Catalog;

internal sealed class Seller : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public SellerKind Kind { get; set; }

    public string WebsiteUrl { get; set; } = string.Empty;

    /// <summary>Affiliate programme used to wrap outbound links, e.g. <c>trendyol-partner</c>.</summary>
    public string? AffiliateNetwork { get; set; }

    public bool IsActive { get; set; } = true;
}
