using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Catalog.ListProducts;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Engagement;

/// <summary>The few product fields every engagement list shows next to its own data. <c>LowestPrice</c> is null when nothing can be bought.</summary>
internal sealed record ProductSummary(Guid Id, string Name, string BrandName, string? PrimaryImageUrl, decimal? LowestPrice);

/// <summary>A product as the catalog list sees it, plus whether it is still listed. Prices are those of the lowest in-stock offer.</summary>
internal sealed record ProductCard(
    Guid Id,
    string Name,
    string Slug,
    BrandRef Brand,
    CategoryRef Category,
    string? PrimaryImageUrl,
    ProductColor PrimaryColor,
    bool IsActive,
    decimal? LowestPrice,
    decimal? LowestOriginalPrice,
    int OfferCount)
{
    public ProductSummary ToSummary() => new(Id, Name, Brand.Name, PrimaryImageUrl, LowestPrice);

    /// <summary>A product nobody sells right now has no price; the list item shape cannot say so, so it reports 0 with an offer count of 0.</summary>
    public ProductListItem ToListItem() => new(
        Id,
        Name,
        Slug,
        Brand,
        Category,
        PrimaryImageUrl,
        PrimaryColor,
        LowestPrice.GetValueOrDefault(),
        LowestOriginalPrice,
        OfferCount,
        ListProductsEndpoint.DiscountPercent(LowestPrice.GetValueOrDefault(), LowestOriginalPrice));
}

internal static class ProductSummaries
{
    /// <summary>Loads cards for the given products in one query, inactive products included. Unknown IDs are simply missing from the result.</summary>
    public static async Task<Dictionary<Guid, ProductCard>> LoadAsync(
        KombinDbContext db, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        var sellableOffers = db.Offers.Where(offer =>
            offer.IsActive && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock));

        var rows = await db.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new
            {
                product.Id,
                product.Name,
                product.Slug,
                product.PrimaryImageUrl,
                product.PrimaryColor,
                product.IsActive,
                BrandId = product.Brand.Id,
                BrandName = product.Brand.Name,
                BrandSlug = product.Brand.Slug,
                CategoryId = product.Category.Id,
                CategorySlug = product.Category.Slug,
                Lowest = sellableOffers.Where(offer => offer.ProductId == product.Id).Min(offer => (decimal?)offer.CurrentPrice),
                LowestOriginal = sellableOffers
                    .Where(offer => offer.ProductId == product.Id)
                    .OrderBy(offer => offer.CurrentPrice)
                    .ThenBy(offer => offer.Id)
                    .Select(offer => offer.OriginalPrice)
                    .FirstOrDefault(),
                OfferCount = sellableOffers.Count(offer => offer.ProductId == product.Id),
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id,
            row => new ProductCard(
                row.Id,
                row.Name,
                row.Slug,
                new BrandRef(row.BrandId, row.BrandName, row.BrandSlug),
                new CategoryRef(row.CategoryId, row.CategorySlug),
                row.PrimaryImageUrl,
                row.PrimaryColor,
                row.IsActive,
                row.Lowest,
                row.LowestOriginal,
                row.OfferCount));
    }
}
