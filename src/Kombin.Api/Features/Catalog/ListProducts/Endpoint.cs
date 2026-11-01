using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Profile;
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

    /// <summary>Best persona match first, then newest. Needs a <c>personaId</c>.</summary>
    Match,
}

/// <summary>
/// <c>PersonaId</c> must belong to the signed-in caller; with it every item carries a <c>matchScore</c> and <c>sort=match</c> is allowed.
/// </summary>
internal sealed record ListProductsQuery(
    string? Query,
    string? CategorySlug,
    string? BrandSlug,
    string? Color,
    string? Audience,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? Sort,
    Guid? PersonaId,
    int Page = 1,
    int PageSize = PagedRequest.DefaultPageSize);

internal static class ListProductsEndpoint
{
    /// <summary>
    /// <c>sort=match</c> scores the newest this many products that pass the filters and pages through them in memory, so it can neither
    /// see further back nor report a larger total. TODO(Recommendation task): score in SQL or from a precomputed table.
    /// </summary>
    public const int MatchCandidateLimit = 500;

    private const string LikeEscape = "\\";

    /// <summary>
    /// A catalog row with everything the list item and the match score need. Built with an object initializer on purpose: EF can
    /// filter and order a query by the members of a member-init projection, but not by those of a constructor call.
    /// </summary>
    private sealed record ListRow
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Slug { get; init; } = string.Empty;

        public DateTimeOffset CreatedAt { get; init; }

        public string? PrimaryImageUrl { get; init; }

        public ProductColor PrimaryColor { get; init; }

        public ProductPattern Pattern { get; init; }

        public ProductFit Fit { get; init; }

        public short Formality { get; init; }

        public Season Seasons { get; init; }

        public Guid BrandId { get; init; }

        public string BrandName { get; init; } = string.Empty;

        public string BrandSlug { get; init; } = string.Empty;

        public Guid CategoryId { get; init; }

        public string CategorySlug { get; init; } = string.Empty;

        public decimal? Lowest { get; init; }

        public decimal? LowestOriginal { get; init; }

        public int OfferCount { get; init; }

        public ProductTraits Traits => new(PrimaryColor, Formality, Fit, Seasons, Pattern, CategorySlug);

        public ProductListItem ToItem(int? matchScore) => new(
            Id,
            Name,
            Slug,
            new BrandRef(BrandId, BrandName, BrandSlug),
            new CategoryRef(CategoryId, CategorySlug),
            PrimaryImageUrl,
            PrimaryColor,
            Lowest.GetValueOrDefault(),
            LowestOriginal,
            OfferCount,
            DiscountPercent(Lowest.GetValueOrDefault(), LowestOriginal),
            matchScore);
    }

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

    private static async Task<IResult> Handle(
        [AsParameters] ListProductsQuery query, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var paging = new PagedRequest(query.Page, query.PageSize);

        PersonaScoring? scoring = null;
        if (query.PersonaId is { } personaId)
        {
            if (currentUser.UserIdOrNull is not { } userId)
            {
                return ProfileErrors.PersonaRequiresAuth.ToHttpResult();
            }

            scoring = await PersonaScoring.LoadAsync(db, clock, userId, personaId, cancellationToken);
            if (scoring is null)
            {
                return ProfileErrors.PersonaNotFound.ToHttpResult();
            }
        }

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
            .Select(product => new ListRow
            {
                Id = product.Id,
                Name = product.Name,
                Slug = product.Slug,
                CreatedAt = product.CreatedAt,
                PrimaryImageUrl = product.PrimaryImageUrl,
                PrimaryColor = product.PrimaryColor,
                Pattern = product.Pattern,
                Fit = product.Fit,
                Formality = product.Formality,
                Seasons = product.Seasons,
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

        if (sort == ProductSort.Match && scoring is not null)
        {
            // Scores live in memory, so the ordering does too: score the newest candidates, order them, then page.
            var candidates = await rows
                .OrderByDescending(row => row.CreatedAt)
                .ThenByDescending(row => row.Id)
                .Take(MatchCandidateLimit)
                .ToListAsync(cancellationToken);

            // OrderByDescending is stable, so equal scores stay newest first.
            var scored = candidates
                .Select(row => (Row: row, Score: scoring.Score(row.Traits)))
                .OrderByDescending(candidate => candidate.Score)
                .ToList();

            var matchItems = scored
                .Skip(paging.Skip)
                .Take(paging.EffectivePageSize)
                .Select(candidate => candidate.Row.ToItem(candidate.Score))
                .ToList();

            return Results.Ok(PagedResponse<ProductListItem>.Create(matchItems, paging, scored.Count));
        }

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
            .Select(row => row.ToItem(scoring?.Score(row.Traits)))
            .ToList();

        return Results.Ok(PagedResponse<ProductListItem>.Create(items, paging, totalCount));
    }
}
