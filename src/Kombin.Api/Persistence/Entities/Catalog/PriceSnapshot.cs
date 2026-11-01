namespace Kombin.Api.Persistence.Entities.Catalog;

/// <summary>Append-only price observation. Not a <see cref="BaseEntity"/>: the series is large and never updated.</summary>
internal sealed class PriceSnapshot
{
    public long Id { get; set; }

    public Guid OfferId { get; set; }

    public decimal Price { get; set; }

    public decimal? OriginalPrice { get; set; }

    public StockStatus StockStatus { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public Offer Offer { get; set; } = null!;
}
