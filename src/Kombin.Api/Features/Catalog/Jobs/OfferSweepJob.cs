using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Features.Engagement;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.Jobs;

internal readonly record struct SweepSummary(int OffersDeactivated, int ProductsDeactivated, int ProductsReactivated);

/// <summary>
/// Retires listings that disappeared: an active offer not seen for 48 hours (<see cref="OfferSweepRules"/>) is deactivated, gets a
/// closing out-of-stock price snapshot and goes through <see cref="AlertEvaluator"/> with <see cref="EvaluationReason.Sweep"/>, which
/// fires no alert (the next upsert that finds the offer in stock is what fires back-in-stock and price-drop). Afterwards every
/// product's <c>IsActive</c> is repaired to match its offers, in both directions. Idempotent: a rerun finds nothing left to do.
/// </summary>
internal sealed partial class OfferSweepJob(KombinDbContext db, IClock clock, AlertEvaluator alerts, ILogger<OfferSweepJob> logger)
{
    public const string JobId = "offer-sweep";

    /// <summary>Daily at 04:30 in Europe/Istanbul (the zone is set where the job is registered, in <c>UseCatalogJobs</c>).</summary>
    public const string Cron = "30 4 * * *";

    public const int BatchSize = 500;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await SweepAsync(null, cancellationToken);

        LogSwept(logger, summary.OffersDeactivated, summary.ProductsDeactivated, summary.ProductsReactivated);
    }

    /// <summary>
    /// Sweeps in batches of <see cref="BatchSize"/>. <paramref name="sellerId"/> restricts it to the offers of one seller and to the
    /// products that have one (the on-demand endpoint, tests).
    /// </summary>
    public async Task<SweepSummary> SweepAsync(Guid? sellerId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var offersDeactivated = await DeactivateStaleOffersAsync(sellerId, now, cancellationToken);
        var (productsDeactivated, productsReactivated) = await RepairProductActivityAsync(sellerId, cancellationToken);

        return new SweepSummary(offersDeactivated, productsDeactivated, productsReactivated);
    }

    private async Task<int> DeactivateStaleOffersAsync(Guid? sellerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deactivated = 0;
        var isStale = OfferSweepRules.Stale(now).Compile();

        while (true)
        {
            var stale = db.Offers.Where(OfferSweepRules.Stale(now));
            if (sellerId is { } onlySeller)
            {
                stale = stale.Where(offer => offer.SellerId == onlySeller);
            }

            // The candidates only: their IDs, not entities. A worker may sight any of them from here on, so they are read again under a lock
            // below. Offers sighted since drop out of this filter, so every pass makes progress.
            var candidates = await stale
                .OrderBy(offer => offer.LastSeenAt)
                .ThenBy(offer => offer.Id)
                .Select(offer => offer.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0)
            {
                return deactivated;
            }

            List<(Offer Offer, decimal Price, StockStatus Stock)> before;

            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                // Row locks, then the rule again on what is really there: an offer an upsert refreshed since the read above is no longer stale and
                // is left alone, and an upsert that arrives now waits for this transaction instead of being overwritten by it. What is
                // deactivated, and what its closing snapshot says, is exactly what was read under the lock.
                var locked = await db.Offers
                    .FromSql($"SELECT * FROM catalog.offers WHERE id = ANY({candidates.ToArray()}) ORDER BY id FOR UPDATE")
                    .ToListAsync(cancellationToken);
                var batch = locked.Where(isStale).ToList();

                // The alert evaluation needs each offer's seller: tracked entities are fixed up with it.
                var sellerIds = batch.Select(offer => offer.SellerId).Distinct().ToList();
                _ = await db.Sellers.Where(seller => sellerIds.Contains(seller.Id)).ToListAsync(cancellationToken);

                before = [.. batch.Select(offer => (Offer: offer, Price: EffectivePrice(offer), offer.StockStatus))];

                foreach (var offer in batch)
                {
                    db.PriceSnapshots.Add(OfferSweepRules.Deactivate(offer, now));
                }

                // One save is one transaction: an offer is never deactivated without its closing snapshot.
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            // After the commit, so an alert problem can neither roll back nor poison the sweep. A sweep is not something a user watches
            // happen, so no alert fires, "any change" included. The offer is now saved out of stock at the same price, which is the
            // "previous" state the next upsert is compared with: back-in-stock and price-drop re-arm on the next sighting.
            foreach (var (offer, price, stock) in before)
            {
                await alerts.EvaluateAsync(offer, price, stock, EvaluationReason.Sweep, cancellationToken);
            }

            db.ChangeTracker.Clear();
            deactivated += before.Count;
        }
    }

    private async Task<(int Deactivated, int Reactivated)> RepairProductActivityAsync(Guid? sellerId, CancellationToken cancellationToken)
    {
        var deactivated = 0;
        var reactivated = 0;

        while (true)
        {
            // Only the products that disagree with their offers; the rule itself is OfferSweepRules.RepairActivity.
            var mismatched = db.Products.Where(product =>
                (product.IsActive && !product.Offers.Any(offer => offer.IsActive))
                || (!product.IsActive && product.Offers.Any(offer => offer.IsActive)));

            if (sellerId is { } onlySeller)
            {
                mismatched = mismatched.Where(product => product.Offers.Any(offer => offer.SellerId == onlySeller));
            }

            var batch = await mismatched
                .Include(product => product.Offers)
                .OrderBy(product => product.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            var changed = batch.Where(OfferSweepRules.RepairActivity).ToList();
            if (changed.Count == 0)
            {
                return (deactivated, reactivated);
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            var nowActive = changed.Count(product => product.IsActive);
            reactivated += nowActive;
            deactivated += changed.Count - nowActive;
        }
    }

    private static decimal EffectivePrice(Offer offer) =>
        GetProductEndpoint.EffectivePrice(offer.CurrentPrice, offer.ShippingCost, offer.FreeShippingThreshold);

    [LoggerMessage(Level = LogLevel.Information, Message = "Offer sweep: {Offers} offer(s) deactivated, {ProductsOff} product(s) deactivated, {ProductsOn} product(s) reactivated.")]
    private static partial void LogSwept(ILogger logger, int offers, int productsOff, int productsOn);
}
