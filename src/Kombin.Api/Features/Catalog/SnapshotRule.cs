using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog;

/// <summary>Decides whether an observation is worth a new <see cref="PriceSnapshot"/> row.</summary>
internal static class SnapshotRule
{
    /// <summary>The series proves "unchanged" at least this often.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// True when there is no snapshot yet, when price, original price or stock status differ from the last one,
    /// or when the last one is older than <see cref="MaxAge"/>.
    /// </summary>
    public static bool ShouldWrite(
        PriceSnapshot? last,
        decimal price,
        decimal? originalPrice,
        StockStatus stockStatus,
        DateTimeOffset now) =>
        last is null
        || last.Price != price
        || last.OriginalPrice != originalPrice
        || last.StockStatus != stockStatus
        || now - last.ObservedAt > MaxAge;
}
