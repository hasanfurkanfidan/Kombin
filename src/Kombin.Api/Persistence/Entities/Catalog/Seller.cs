namespace Kombin.Api.Persistence.Entities.Catalog;

internal sealed class Seller : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public SellerKind Kind { get; set; }

    public string WebsiteUrl { get; set; } = string.Empty;

    /// <summary>Affiliate programme used to wrap outbound links, e.g. <c>trendyol-partner</c>.</summary>
    public string? AffiliateNetwork { get; set; }

    /// <summary>
    /// How <c>/go/{offerId}</c> builds the outbound URL, with the placeholders <c>{url}</c> (offer URL, URL-encoded),
    /// <c>{rawUrl}</c>, <c>{clickId}</c>, <c>{source}</c> and <c>{p.key}</c> for every key of <see cref="AffiliateParams"/>.
    /// Null means the default: the offer URL with <c>utm_*</c> and <c>kombin_click</c> parameters appended.
    /// </summary>
    public string? AffiliateUrlTemplate { get; set; }

    /// <summary>Free key/values (affiliate id, campaign) that a template reads as <c>{p.key}</c>.</summary>
    public Dictionary<string, string>? AffiliateParams { get; set; }

    public bool IsActive { get; set; } = true;
}
