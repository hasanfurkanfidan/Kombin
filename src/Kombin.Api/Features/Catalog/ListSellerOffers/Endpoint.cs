using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.ListSellerOffers;

internal sealed record ListSellerOffersQuery(
    DateTimeOffset? UpdatedAfter,
    int Page = 1,
    int PageSize = ListSellerOffersEndpoint.DefaultPageSize);

internal sealed record SellerOfferItem(
    string ExternalId,
    Guid ProductId,
    string? ModelCode,
    decimal CurrentPrice,
    StockStatus StockStatus,
    DateTimeOffset LastSeenAt,
    bool IsActive);

internal static class ListSellerOffersEndpoint
{
    public const int DefaultPageSize = 100;

    public const int MaxPageSize = 1000;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/sellers/{sellerSlug}/offers", Handle)
            .WithName("ListSellerOffers")
            .Produces<PagedResponse<SellerOfferItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Everything stored for one seller, inactive listings included, ordered by external ID so that paging is stable while workers
    /// upsert. <c>updatedAfter</c> keeps the listings changed since then: an upsert and the sweep both move <c>UpdatedAt</c>, so
    /// a worker sees re-priced listings and the ones that vanished.
    /// </summary>
    private static async Task<IResult> Handle(string sellerSlug, [AsParameters] ListSellerOffersQuery query, KombinDbContext db, CancellationToken cancellationToken)
    {
        var slug = sellerSlug.Trim();
        var sellerId = await db.Sellers.Where(seller => seller.Slug == slug).Select(seller => (Guid?)seller.Id).SingleOrDefaultAsync(cancellationToken);

        if (sellerId is null)
        {
            return CatalogErrors.SellerNotFound.ToHttpResult();
        }

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var offers = db.Offers.AsNoTracking().Where(offer => offer.SellerId == sellerId);
        if (query.UpdatedAfter is { } updatedAfter)
        {
            offers = offers.Where(offer => offer.UpdatedAt > updatedAfter);
        }

        var totalCount = await offers.CountAsync(cancellationToken);
        var items = await offers
            .OrderBy(offer => offer.ExternalId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(offer => new SellerOfferItem(
                offer.ExternalId,
                offer.ProductId,
                offer.Product.ModelCode,
                offer.CurrentPrice,
                offer.StockStatus,
                offer.LastSeenAt,
                offer.IsActive))
            .ToListAsync(cancellationToken);

        return Results.Ok(new PagedResponse<SellerOfferItem>(items, page, pageSize, totalCount));
    }
}
