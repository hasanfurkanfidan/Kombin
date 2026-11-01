using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.GetPriceHistory;

internal sealed record SellerRef(Guid Id, string Name, string Slug, SellerKind Kind);

internal sealed record PricePoint(DateOnly Date, decimal MinPrice, StockStatus StockStatus);

internal sealed record OfferSeries(Guid OfferId, SellerRef Seller, IReadOnlyList<PricePoint> Points);

internal static class GetPriceHistoryEndpoint
{
    public const int DefaultDays = 90;

    public const int MinDays = 7;

    public const int MaxDays = 365;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/products/{id:guid}/price-history", Handle)
            .WithName("GetPriceHistory")
            .Produces<IReadOnlyList<OfferSeries>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(
        Guid id, KombinDbContext db, IClock clock, CancellationToken cancellationToken, int days = DefaultDays)
    {
        if (!await db.Products.AnyAsync(product => product.Id == id, cancellationToken))
        {
            return CatalogErrors.ProductNotFound.ToHttpResult();
        }

        var from = clock.UtcNow.WindowStart(Math.Clamp(days, MinDays, MaxDays));

        var offers = await db.Offers
            .AsNoTracking()
            .Include(offer => offer.Seller)
            .Where(offer => offer.ProductId == id && offer.IsActive)
            .ToListAsync(cancellationToken);

        var snapshots = await db.PriceSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.Offer.ProductId == id && snapshot.Offer.IsActive && snapshot.ObservedAt >= from)
            .Select(snapshot => new { snapshot.OfferId, snapshot.Price, snapshot.StockStatus, snapshot.ObservedAt })
            .ToListAsync(cancellationToken);

        var snapshotsByOffer = snapshots.ToLookup(snapshot => snapshot.OfferId);

        var series = offers
            .OrderBy(offer => offer.Seller.Name, StringComparer.Ordinal)
            .ThenBy(offer => offer.Id)
            .Select(offer => new OfferSeries(
                offer.Id,
                new SellerRef(offer.Seller.Id, offer.Seller.Name, offer.Seller.Slug, offer.Seller.Kind),
                // One point per UTC calendar day: that day's cheapest observation (the latest one on a tie).
                [.. snapshotsByOffer[offer.Id]
                    .GroupBy(snapshot => DateOnly.FromDateTime(snapshot.ObservedAt.UtcDateTime))
                    .OrderBy(day => day.Key)
                    .Select(day =>
                    {
                        var cheapest = day.OrderBy(snapshot => snapshot.Price).ThenByDescending(snapshot => snapshot.ObservedAt).First();
                        return new PricePoint(day.Key, cheapest.Price, cheapest.StockStatus);
                    })]))
            .ToList();

        return Results.Ok(series);
    }
}
