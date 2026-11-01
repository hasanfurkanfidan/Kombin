using System.Text.Json.Serialization;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.GetProduct;

internal sealed record BrandRef(Guid Id, string Name, string Slug);

internal sealed record CategoryRef(Guid Id, string Slug, string Name);

internal sealed record SellerRef(Guid Id, string Name, string Slug, SellerKind Kind);

/// <summary>
/// <c>GoPath</c> (<c>/api/v1/go/{offerId}</c>) is the only way to open an offer: the redirect records the click and adds the affiliate
/// parameters. The seller's own URL is never part of the response.
/// </summary>
internal sealed record OfferResponse(
    Guid Id,
    SellerRef Seller,
    string GoPath,
    decimal CurrentPrice,
    decimal? OriginalPrice,
    string Currency,
    decimal? ShippingCost,
    decimal? FreeShippingThreshold,
    decimal EffectivePrice,
    StockStatus StockStatus,
    IReadOnlyList<string> AvailableSizes,
    DateTimeOffset LastSeenAt);

/// <summary>Computed over in-stock prices only: the lowest of the active offers now, and the extremes seen in snapshots.</summary>
internal sealed record PriceSummary(decimal? LowestNow, decimal? LowestLast90Days, decimal? HighestLast90Days, bool IsLowestOfLast90Days);

/// <summary><c>MatchScore</c> is present when the request named a persona; <c>SizeHint</c> when the caller is signed in. Both are left out otherwise.</summary>
internal sealed record ProductResponse(
    Guid Id,
    string Name,
    string Slug,
    string? ModelCode,
    string? Description,
    BrandRef Brand,
    CategoryRef Category,
    Audience Audience,
    ProductColor PrimaryColor,
    ProductPattern Pattern,
    ProductFit Fit,
    int Formality,
    IReadOnlyList<string> Seasons,
    string? Material,
    bool IsActive,
    string? PrimaryImageUrl,
    IReadOnlyList<string> Images,
    IReadOnlyList<OfferResponse> Offers,
    PriceSummary PriceSummary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MatchScore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SizeHintResponse? SizeHint);

internal static class GetProductEndpoint
{
    public const int SummaryDays = 90;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/products/{id:guid}", Handle)
            .WithName("GetProduct")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>The price plus shipping. Shipping is free when the listing says so, when the price reaches the free-shipping threshold, or when it is unknown.</summary>
    internal static decimal EffectivePrice(decimal price, decimal? shippingCost, decimal? freeShippingThreshold)
    {
        var shipping = freeShippingThreshold is { } threshold && price >= threshold ? 0m : shippingCost ?? 0m;
        return price + shipping;
    }

    internal static bool IsSellable(StockStatus stockStatus) => stockStatus is StockStatus.InStock or StockStatus.LowStock;

    private static async Task<IResult> Handle(
        Guid id, Guid? personaId, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserIdOrNull;

        if (personaId is not null && userId is null)
        {
            return ProfileErrors.PersonaRequiresAuth.ToHttpResult();
        }

        var product = await db.Products
            .AsNoTracking()
            .Include(candidate => candidate.Brand)
            .Include(candidate => candidate.Category)
            .Include(candidate => candidate.Images)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (product is null)
        {
            return CatalogErrors.ProductNotFound.ToHttpResult();
        }

        PersonaScoring? scoring = null;
        if (personaId is { } requestedPersonaId)
        {
            scoring = await PersonaScoring.LoadAsync(db, clock, userId!.Value, requestedPersonaId, cancellationToken);
            if (scoring is null)
            {
                return ProfileErrors.PersonaNotFound.ToHttpResult();
            }
        }

        // Inactive products still resolve (the app may hold a saved link) but only live offers are listed.
        var activeOffers = await db.Offers
            .AsNoTracking()
            .Include(offer => offer.Seller)
            .Where(offer => offer.ProductId == id && offer.IsActive)
            .ToListAsync(cancellationToken);

        var offers = activeOffers
            .Select(offer => new OfferResponse(
                offer.Id,
                new SellerRef(offer.Seller.Id, offer.Seller.Name, offer.Seller.Slug, offer.Seller.Kind),
                $"/api/v1/go/{offer.Id}",
                offer.CurrentPrice,
                offer.OriginalPrice,
                offer.Currency,
                offer.ShippingCost,
                offer.FreeShippingThreshold,
                EffectivePrice(offer.CurrentPrice, offer.ShippingCost, offer.FreeShippingThreshold),
                offer.StockStatus,
                offer.AvailableSizes,
                offer.LastSeenAt))
            .OrderBy(offer => offer.EffectivePrice)
            .ThenBy(offer => offer.CurrentPrice)
            .ThenBy(offer => offer.Seller.Name, StringComparer.Ordinal)
            .ToList();

        var lowestNow = activeOffers
            .Where(offer => IsSellable(offer.StockStatus))
            .Select(offer => (decimal?)offer.CurrentPrice)
            .Min();

        var summary = await BuildSummaryAsync(db, clock, id, lowestNow, cancellationToken);

        var sizeHint = userId is { } signedInUserId
            ? await SizeHints.BuildAsync(db, signedInUserId, product.BrandId, product.Category.Slug, cancellationToken)
            : null;

        return Results.Ok(new ProductResponse(
            product.Id,
            product.Name,
            product.Slug,
            product.ModelCode,
            product.Description,
            new BrandRef(product.Brand.Id, product.Brand.Name, product.Brand.Slug),
            new CategoryRef(product.Category.Id, product.Category.Slug, product.Category.Name),
            product.Audience,
            product.PrimaryColor,
            product.Pattern,
            product.Fit,
            product.Formality,
            EnumNames.SeasonNames(product.Seasons),
            product.Material,
            product.IsActive,
            product.PrimaryImageUrl,
            [.. product.Images.OrderBy(image => image.SortOrder).Select(image => image.Url)],
            offers,
            summary,
            scoring?.Score(product),
            sizeHint));
    }

    private static async Task<PriceSummary> BuildSummaryAsync(
        KombinDbContext db, IClock clock, Guid productId, decimal? lowestNow, CancellationToken cancellationToken)
    {
        var from = clock.UtcNow.WindowStart(SummaryDays);

        var extremes = await db.PriceSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.Offer.ProductId == productId
                && snapshot.ObservedAt >= from
                && (snapshot.StockStatus == StockStatus.InStock || snapshot.StockStatus == StockStatus.LowStock))
            .GroupBy(_ => 1)
            .Select(group => new { Lowest = group.Min(snapshot => snapshot.Price), Highest = group.Max(snapshot => snapshot.Price) })
            .SingleOrDefaultAsync(cancellationToken);

        // The live price always belongs to the window, even if the listing has not been observed (and so not snapshotted) lately.
        decimal? lowest = extremes is null ? lowestNow : Math.Min(extremes.Lowest, lowestNow ?? extremes.Lowest);
        decimal? highest = extremes is null ? lowestNow : Math.Max(extremes.Highest, lowestNow ?? extremes.Highest);

        return new PriceSummary(lowestNow, lowest, highest, lowestNow is not null && lowestNow <= lowest);
    }
}
