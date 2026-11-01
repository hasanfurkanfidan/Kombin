using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;

namespace Kombin.Api.Features.Engagement;

internal sealed record AlertResponse(
    Guid Id,
    AlertKind Kind,
    decimal? TargetPrice,
    IReadOnlyList<string> Sizes,
    bool IsActive,
    int TriggerCount,
    DateTimeOffset? LastTriggeredAt,
    decimal? LastPriceAfter,
    DateTimeOffset CreatedAt,
    ProductSummary Product);

/// <summary>Shared by every endpoint that returns alerts, so they all look the same.</summary>
internal static class AlertResponses
{
    /// <summary>Builds responses in the order of <paramref name="alerts"/> with two queries, however many alerts there are.</summary>
    public static async Task<List<AlertResponse>> BuildAsync(KombinDbContext db, IReadOnlyList<PriceAlert> alerts, CancellationToken cancellationToken)
    {
        if (alerts.Count == 0)
        {
            return [];
        }

        var cards = await ProductSummaries.LoadAsync(db, [.. alerts.Select(alert => alert.ProductId).Distinct()], cancellationToken);

        var alertIds = alerts.Select(alert => alert.Id).ToList();
        var lastPrices = await db.PriceAlerts
            .AsNoTracking()
            .Where(alert => alertIds.Contains(alert.Id))
            .Select(alert => new
            {
                alert.Id,
                LastPriceAfter = db.AlertTriggers
                    .Where(trigger => trigger.PriceAlertId == alert.Id)
                    .OrderByDescending(trigger => trigger.TriggeredAt)
                    .ThenByDescending(trigger => trigger.Id)
                    .Select(trigger => trigger.PriceAfter)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(row => row.Id, row => row.LastPriceAfter, cancellationToken);

        return
        [
            .. alerts.Select(alert => new AlertResponse(
                alert.Id,
                alert.Kind,
                alert.TargetPrice,
                alert.Sizes,
                alert.IsActive,
                alert.TriggerCount,
                alert.LastTriggeredAt,
                lastPrices.GetValueOrDefault(alert.Id),
                alert.CreatedAt,
                cards[alert.ProductId].ToSummary())),
        ];
    }
}

/// <summary>The "is this target a drop?" check that creating and editing a <see cref="AlertKind.PriceDrop"/> alert share.</summary>
internal static class AlertTargets
{
    /// <summary>
    /// True when <paramref name="target"/> is strictly below the lowest effective price among the product's buyable offers.
    /// A product nobody sells right now has no current price, so any target is acceptable.
    /// </summary>
    public static async Task<bool> IsBelowCurrentAsync(KombinDbContext db, Guid productId, decimal target, CancellationToken cancellationToken)
    {
        var offers = await db.Offers
            .AsNoTracking()
            .Where(offer => offer.ProductId == productId
                && offer.IsActive
                && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock))
            .Select(offer => new { offer.CurrentPrice, offer.ShippingCost, offer.FreeShippingThreshold })
            .ToListAsync(cancellationToken);

        return offers.Count == 0
            || target < offers.Min(offer => GetProductEndpoint.EffectivePrice(offer.CurrentPrice, offer.ShippingCost, offer.FreeShippingThreshold));
    }
}
