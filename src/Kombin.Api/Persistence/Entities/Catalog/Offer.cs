namespace Kombin.Api.Persistence.Entities.Catalog;

/// <summary>One seller listing of a <see cref="Product"/>.</summary>
internal sealed class Offer : BaseEntity
{
    public const string DefaultCurrency = "TRY";

    public Guid ProductId { get; set; }

    public Guid SellerId { get; set; }

    /// <summary>The seller's own listing identifier; unique per seller.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The plain product URL; affiliate wrapping happens elsewhere.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The seller's listing title, kept for debugging and matching.</summary>
    public string Title { get; set; } = string.Empty;

    public decimal CurrentPrice { get; set; }

    /// <summary>The seller's crossed-out price.</summary>
    public decimal? OriginalPrice { get; set; }

    public string Currency { get; set; } = DefaultCurrency;

    public decimal? ShippingCost { get; set; }

    public decimal? FreeShippingThreshold { get; set; }

    public StockStatus StockStatus { get; set; }

    public string[] AvailableSizes { get; set; } = [];

    /// <summary>When a worker last observed the listing.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>False once the listing has disappeared.</summary>
    public bool IsActive { get; set; } = true;

    public Product Product { get; set; } = null!;

    public Seller Seller { get; set; } = null!;
}
