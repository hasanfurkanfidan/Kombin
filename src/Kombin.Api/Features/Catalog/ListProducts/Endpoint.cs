using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.ListProducts;

internal enum ProductSort
{
    /// <summary>Newest first for now; a real relevance score comes with search.</summary>
    Relevance,
    PriceAsc,
    PriceDesc,
    Newest,
}

internal sealed record ListProductsQuery(
    string? Query,
    string? CategorySlug,
    string? BrandSlug,
    string? Color,
    string? Audience,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? Sort,
    int Page = 1,
    int PageSize = PagedRequest.DefaultPageSize);

internal static class ListProductsEndpoint
{
    private const string LikeEscape = "\\";

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/products", Handle)
            .WithName("ListProducts")
            .WithValidation<ListProductsQuery>()
            .Produces<PagedResponse<ProductListItem>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

    internal static int DiscountPercent(decimal lowestPrice, decimal? originalPrice) =>
        originalPrice is > 0m && originalPrice > lowestPrice
            ? (int)Math.Round((originalPrice.Value - lowestPrice) / originalPrice.Value * 100m, MidpointRounding.AwayFromZero)
            : 0;

    private static async Task<IResult> Handle([AsParameters] ListProductsQuery query, KombinDbContext db, CancellationToken cancellationToken)
    {
        var paging = new PagedRequest(query.Page, query.PageSize);
        var products = db.Products.AsNoTracking().Where(product => product.IsActive);

        var term = query.Query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var escaped = term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            var contains = $"%{escaped}%";

            products = products.Where(product =>
                EF.Functions.ILike(product.Name, contains, LikeEscape) || EF.Functions.ILike(product.Brand.Name, contains, LikeEscape));
        }

        if (!string.IsNullOrWhiteSpace(query.CategorySlug))
        {
            var categories = await db.Categories
                .AsNoTracking()
                .Select(category => new { category.Id, category.ParentId, category.Slug })
                .ToListAsync(cancellationToken);

            var root = categories.FirstOrDefault(category => category.Slug == query.CategorySlug.Trim());
            if (root is null)
            {
                return CatalogErrors.CategoryNotFound.ToHttpResult();
            }

            // The category itself plus every descendant, so asking for a parent includes its children.
            var categoryIds = new List<Guid> { root.Id };
            for (var index = 0; index < categoryIds.Count; index++)
            {
                var parentId = categoryIds[index];
                categoryIds.AddRange(categories.Where(category => category.ParentId == parentId).Select(category => category.Id));
            }

            products = products.Where(product => categoryIds.Contains(product.CategoryId));
        }

        if (!string.IsNullOrWhiteSpace(query.BrandSlug))
        {
            var brandSlug = query.BrandSlug.Trim();
            var brandId = await db.Brands
                .Where(brand => brand.Slug == brandSlug)
                .Select(brand => (Guid?)brand.Id)
                .SingleOrDefaultAsync(cancellationToken);

            if (brandId is null)
            {
                return CatalogErrors.BrandNotFound.ToHttpResult();
            }

            products = products.Where(product => product.BrandId == brandId);
        }

        if (EnumNames.TryParse<ProductColor>(query.Color, out var color))
        {
            products = products.Where(product => product.PrimaryColor == color);
        }

        if (EnumNames.TryParse<Audience>(query.Audience, out var audience))
        {
            products = products.Where(product => product.Audience == audience);
        }

        var sellableOffers = db.Offers.Where(offer =>
            offer.IsActive && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock));

        // One statement: the lowest price, the lowest offer's crossed-out price and the offer count are correlated subqueries.
        var rows = products
            .Select(product => new
            {
                product.Id,
                product.Name,
                product.Slug,
                product.CreatedAt,
                product.PrimaryImageUrl,
                product.PrimaryColor,
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
            .Where(row => row.Lowest != null);

        if (query.MinPrice is { } minPrice)
        {
            rows = rows.Where(row => row.Lowest >= minPrice);
        }

        if (query.MaxPrice is { } maxPrice)
        {
            rows = rows.Where(row => row.Lowest <= maxPrice);
        }

        var sort = EnumNames.TryParse<ProductSort>(query.Sort, out var requestedSort) ? requestedSort : ProductSort.Relevance;

        var ordered = sort switch
        {
            ProductSort.PriceAsc => rows.OrderBy(row => row.Lowest).ThenBy(row => row.Id),
            ProductSort.PriceDesc => rows.OrderByDescending(row => row.Lowest).ThenBy(row => row.Id),
            _ => rows.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id),
        };

        var totalCount = await rows.CountAsync(cancellationToken);
        var page = await ordered.Skip(paging.Skip).Take(paging.EffectivePageSize).ToListAsync(cancellationToken);

        // Lowest is never null here: rows without a sellable offer were filtered out above.
        var items = page
            .Select(row => new ProductListItem(
                row.Id,
                row.Name,
                row.Slug,
                new BrandRef(row.BrandId, row.BrandName, row.BrandSlug),
                new CategoryRef(row.CategoryId, row.CategorySlug),
                row.PrimaryImageUrl,
                row.PrimaryColor,
                row.Lowest.GetValueOrDefault(),
                row.LowestOriginal,
                row.OfferCount,
                DiscountPercent(row.Lowest.GetValueOrDefault(), row.LowestOriginal)))
            .ToList();

        return Results.Ok(PagedResponse<ProductListItem>.Create(items, paging, totalCount));
    }
}
