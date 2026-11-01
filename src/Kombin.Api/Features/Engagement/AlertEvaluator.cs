using System.Text.Json;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;

namespace Kombin.Api.Features.Engagement;

/// <summary>What a rule found out: the prices to report and the details that go into <see cref="AlertTrigger.Payload"/>.</summary>
internal sealed record AlertHit(decimal? PriceBefore, decimal? PriceAfter, IReadOnlyList<string> Sizes);

/// <summary>Why an offer is being evaluated.</summary>
internal enum EvaluationReason
{
    /// <summary>A worker observed the listing and it changed: the alert rules apply.</summary>
    Observation,

    /// <summary>The offer sweep retired the listing: nothing a user watches was observed, so no alert may fire.</summary>
    Sweep,
}

/// <summary>
/// Decides which active alerts of a product fire when one of its offers changes, and records the triggers. Runs inside the
/// catalog upsert path (<c>OfferUpserter</c>), never on the request of an app user, and has no HTTP concerns. Prices are
/// effective prices (price plus shipping), as everywhere in Catalog.
/// </summary>
internal sealed partial class AlertEvaluator(KombinDbContext db, IClock clock, ILogger<AlertEvaluator> logger)
{
    /// <summary>The smallest relative price change that counts for <see cref="AlertKind.AnyChange"/>.</summary>
    public const decimal AnyChangeThreshold = 0.01m;

    /// <summary>
    /// Evaluates the active alerts of the offer's product against a change that was just stored. <paramref name="previousPrice"/>
    /// is the offer's previous <em>effective</em> price; null means the offer was not observed before, which fires nothing.
    /// Never throws (except on cancellation): a failure is logged and the caller carries on.
    /// </summary>
    /// <remarks>
    /// For <see cref="EvaluationReason.Sweep"/> every rule is skipped. The evaluator keeps no state of its own: the "previous" side of
    /// the next evaluation is the offer's stored stock and price, which the sweep has already set (out of stock, price unchanged) by the
    /// time it calls this, so back-in-stock and price-drop re-arm on the next sighting without anything being written here.
    /// </remarks>
    public async Task EvaluateAsync(
        Offer offer, decimal? previousPrice, StockStatus previousStock, EvaluationReason reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (reason == EvaluationReason.Sweep)
        {
            return;
        }

        try
        {
            var alerts = await db.PriceAlerts
                .Where(alert => alert.ProductId == offer.ProductId && alert.IsActive)
                .ToListAsync(cancellationToken);

            var now = clock.UtcNow;
            var fired = 0;

            foreach (var alert in alerts)
            {
                if (Evaluate(alert, offer, previousPrice, previousStock) is not { } hit)
                {
                    continue;
                }

                db.AlertTriggers.Add(new AlertTrigger
                {
                    PriceAlertId = alert.Id,
                    UserId = alert.UserId,
                    ProductId = alert.ProductId,
                    OfferId = offer.Id,
                    Kind = alert.Kind,
                    TriggeredAt = now,
                    PriceBefore = hit.PriceBefore,
                    PriceAfter = hit.PriceAfter,
                    Payload = BuildPayload(offer, previousStock, hit),
                });

                // The alert stays active: the user switches it off, and PriceDrop's crossing rule stops it re-firing on its own.
                alert.LastTriggeredAt = now;
                alert.TriggerCount++;
                fired++;
            }

            if (fired > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                LogTriggered(logger, fired, offer.Id);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Drop whatever this evaluation staged so the caller's next save does not trip over it.
            foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.Entity is PriceAlert or AlertTrigger).ToList())
            {
                entry.State = EntityState.Detached;
            }

            LogEvaluationFailed(logger, exception, offer.Id);
        }
    }

    /// <summary>The rules, free of any I/O. Returns null when the alert does not fire for this change.</summary>
    internal static AlertHit? Evaluate(PriceAlert alert, Offer offer, decimal? previousPrice, StockStatus previousStock)
    {
        // Without a previous observation there is no change to speak of.
        if (previousPrice is not { } before)
        {
            return null;
        }

        var after = GetProductEndpoint.EffectivePrice(offer.CurrentPrice, offer.ShippingCost, offer.FreeShippingThreshold);
        var stock = offer.StockStatus;

        return alert.Kind switch
        {
            AlertKind.PriceDrop => EvaluatePriceDrop(alert, before, previousStock, after, stock),
            AlertKind.BackInStock => EvaluateBackInStock(alert, offer, before, previousStock, after),
            AlertKind.AnyChange => EvaluateAnyChange(before, previousStock, after, stock),
            _ => null,
        };
    }

    /// <summary>
    /// Fires when the offer crosses the target from above while it can be bought. A price seen while the offer was out of
    /// stock does not count as "already below", so an offer that comes back at a price under the target fires too.
    /// </summary>
    private static AlertHit? EvaluatePriceDrop(PriceAlert alert, decimal before, StockStatus previousStock, decimal after, StockStatus stock)
    {
        if (alert.TargetPrice is not { } target || !GetProductEndpoint.IsSellable(stock) || after > target)
        {
            return null;
        }

        var wasBelow = GetProductEndpoint.IsSellable(previousStock) && before <= target;

        return wasBelow ? null : new AlertHit(before, after, []);
    }

    private static AlertHit? EvaluateBackInStock(PriceAlert alert, Offer offer, decimal before, StockStatus previousStock, decimal after)
    {
        if (GetProductEndpoint.IsSellable(previousStock) || !GetProductEndpoint.IsSellable(offer.StockStatus))
        {
            return null;
        }

        if (alert.Sizes.Length == 0)
        {
            return new AlertHit(before, after, offer.AvailableSizes);
        }

        string[] matching = [.. alert.Sizes.Where(wanted => offer.AvailableSizes.Contains(wanted, StringComparer.OrdinalIgnoreCase))];

        return matching.Length == 0 ? null : new AlertHit(before, after, matching);
    }

    private static AlertHit? EvaluateAnyChange(decimal before, StockStatus previousStock, decimal after, StockStatus stock)
    {
        var stockChanged = previousStock != stock;
        var priceChanged = before == 0m ? after != 0m : Math.Abs(after - before) / before >= AnyChangeThreshold;

        return stockChanged || priceChanged ? new AlertHit(before, after, []) : null;
    }

    private static string BuildPayload(Offer offer, StockStatus previousStock, AlertHit hit) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sellerId"] = offer.SellerId,
            ["sellerSlug"] = offer.Seller?.Slug,
            ["stockBefore"] = previousStock.ToString(),
            ["stockAfter"] = offer.StockStatus.ToString(),
            ["sizes"] = hit.Sizes,
        });

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Count} alert(s) fired for offer {OfferId}.")]
    private static partial void LogTriggered(ILogger logger, int count, Guid offerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Alert evaluation failed for offer {OfferId}; the upsert continues.")]
    private static partial void LogEvaluationFailed(ILogger logger, Exception exception, Guid offerId);
}
