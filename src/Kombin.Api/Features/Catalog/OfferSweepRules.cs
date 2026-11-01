using System.Linq.Expressions;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog;

/// <summary>The decisions of the offer sweep, free of any I/O so they can be tested (and, for <see cref="Stale"/>, translated to SQL).</summary>
internal static class OfferSweepRules
{
    /// <summary>An active offer that no worker has seen for longer than this is taken to be gone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    /// <summary>Active offers last seen strictly more than <see cref="StaleAfter"/> before <paramref name="now"/>; exactly 48 hours is still fresh.</summary>
    public static Expression<Func<Offer, bool>> Stale(DateTimeOffset now)
    {
        var cutoff = now - StaleAfter;

        return offer => offer.IsActive && offer.LastSeenAt < cutoff;
    }

    /// <summary>
    /// Takes the offer out of sale: inactive and out of stock, so a later sighting is a restock to the alert rules. Returns the
    /// snapshot that closes its price history (same price, out of stock), so the history shows the gap.
    /// </summary>
    public static PriceSnapshot Deactivate(Offer offer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(offer);

        offer.IsActive = false;
        offer.StockStatus = StockStatus.OutOfStock;

        return new PriceSnapshot
        {
            OfferId = offer.Id,
            Price = offer.CurrentPrice,
            OriginalPrice = offer.OriginalPrice,
            StockStatus = StockStatus.OutOfStock,
            ObservedAt = now,
        };
    }

    /// <summary>
    /// Makes <see cref="Product.IsActive"/> say whether the product has an active offer, in both directions: a product that lost its
    /// last offer goes inactive, one that has an active offer again (or never lost it) goes active. The offers must be loaded.
    /// Returns whether anything changed.
    /// </summary>
    public static bool RepairActivity(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var shouldBeActive = product.Offers.Any(offer => offer.IsActive);
        if (product.IsActive == shouldBeActive)
        {
            return false;
        }

        product.IsActive = shouldBeActive;
        return true;
    }
}
