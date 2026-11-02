using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>A catalog piece's cheapest offer when it was suggested. <c>GoPath</c> is the only way to open it (the click is recorded).</summary>
internal sealed record OutfitOfferResponse(Guid OfferId, SellerRef? Seller, decimal Price, string GoPath);

/// <summary>
/// One piece. A wardrobe piece has <c>WardrobeItemId</c> and its thumbnail; a catalog piece (<c>Role</c> Gap, or an Anchor from the
/// catalog) has <c>ProductId</c>, the product image and <c>Offer</c>.
/// </summary>
internal sealed record OutfitPieceResponse(
    OutfitSlot Slot,
    OutfitItemRole Role,
    PieceSource Source,
    Guid? WardrobeItemId,
    Guid? ProductId,
    string Name,
    string? CategorySlug,
    ProductColor Color,
    string? ImageUrl,
    OutfitOfferResponse? Offer);

/// <summary>The forecast the outfit was built for. <c>TemperatureC</c> is the day's dressing temperature, <c>(2·max + min) / 3</c>.</summary>
internal sealed record OutfitWeatherResponse(
    string City,
    DateOnly Date,
    double TempMinC,
    double TempMaxC,
    int TemperatureC,
    TemperatureBand Band,
    bool Rain,
    int? PrecipitationProbability)
{
    public static OutfitWeatherResponse From(WeatherSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var conditions = WeatherRules.From(snapshot);

        return new OutfitWeatherResponse(
            snapshot.City,
            snapshot.Date,
            snapshot.TempMinC,
            snapshot.TempMaxC,
            conditions.TemperatureC ?? 0,
            conditions.Band ?? TemperatureBand.Mild,
            conditions.Rain,
            snapshot.PrecipitationProbability);
    }
}

internal sealed record OutfitNarrationResponse(string Text, NarrationProvider Provider);

/// <summary>
/// An outfit. <c>CompletionPrice</c> is what the gap pieces cost (their cheapest offers, effective price), null when there are none;
/// <c>AnchorPrice</c> is the catalog anchor's price; <c>TotalPrice</c> both together. <c>Rule</c> names why it works.
/// </summary>
internal sealed record OutfitResponse(
    Guid Id,
    OutfitKind Kind,
    OutfitStatus Status,
    OutfitCompleteness Completeness,
    Occasion? Occasion,
    DateOnly Date,
    int Seed,
    int Score,
    string Rule,
    Guid? PersonaId,
    OutfitWeatherResponse? Weather,
    IReadOnlyList<OutfitPieceResponse> Pieces,
    decimal? CompletionPrice,
    decimal? AnchorPrice,
    decimal? TotalPrice,
    OutfitNarrationResponse Narration,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SavedAt,
    DateTimeOffset? WornAt);

internal static class OutfitResponses
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string? SerializeWeather(WeatherSnapshot? snapshot) => snapshot is null ? null : JsonSerializer.Serialize(snapshot, Json);

    public static WeatherSnapshot? ReadWeather(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<WeatherSnapshot>(json, Json);

    /// <summary>Builds the responses in order, with a fixed number of queries whatever the count. The outfits' <c>Items</c> must be loaded.</summary>
    public static async Task<List<OutfitResponse>> BuildAsync(
        KombinDbContext db, IObjectStorage storage, IReadOnlyList<Outfit> outfits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outfits);

        var items = outfits.SelectMany(outfit => outfit.Items).ToList();
        var wardrobeIds = items.Select(item => item.WardrobeItemId).OfType<Guid>().Distinct().ToList();
        var productIds = items.Select(item => item.ProductId).OfType<Guid>().Distinct().ToList();
        var offerIds = items.Select(item => item.OfferId).OfType<Guid>().Distinct().ToList();

        var wardrobeItems = await db.WardrobeItems.AsNoTracking().Where(item => wardrobeIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var summaries = (await WardrobeItemResponses.SummarizeAsync(db, storage, wardrobeItems, cancellationToken)).ToDictionary(summary => summary.Id);

        var products = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new
            {
                product.Id,
                product.Name,
                BrandName = product.Brand.Name,
                CategorySlug = product.Category.Slug,
                product.PrimaryColor,
                product.PrimaryImageUrl,
            })
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var sellers = await db.Offers.AsNoTracking()
            .Where(offer => offerIds.Contains(offer.Id))
            .Select(offer => new { offer.Id, offer.Seller.Name, offer.Seller.Slug })
            .ToDictionaryAsync(offer => offer.Id, offer => new SellerRef(offer.Name, offer.Slug), cancellationToken);

        OutfitPieceResponse Piece(OutfitItem item)
        {
            if (item.WardrobeItemId is { } wardrobeId)
            {
                var summary = summaries.GetValueOrDefault(wardrobeId);
                var colour = summary?.PrimaryColor ?? ProductColor.Other;
                var slug = summary?.CategorySlug;
                var name = summary?.Name ?? (slug is null ? "Parça" : TurkishGrammar.Capitalize(TurkishGrammar.Phrase(colour, slug)));

                return new OutfitPieceResponse(item.Slot, item.Role, PieceSource.Wardrobe, wardrobeId, null, name, slug, colour, summary?.ThumbUrl, null);
            }

            var product = products.GetValueOrDefault(item.ProductId!.Value);
            var offer = item.OfferId is { } offerId && item.Price is { } price
                ? new OutfitOfferResponse(offerId, sellers.GetValueOrDefault(offerId), price, $"/api/v1/go/{offerId}")
                : null;

            return new OutfitPieceResponse(
                item.Slot,
                item.Role,
                PieceSource.Catalog,
                null,
                item.ProductId,
                product is null ? "Ürün" : Copy.ProductDisplayName(product.BrandName, product.Name),
                product?.CategorySlug,
                product?.PrimaryColor ?? ProductColor.Other,
                product?.PrimaryImageUrl,
                offer);
        }

        return
        [
            .. outfits.Select(outfit =>
            {
                var ordered = outfit.Items.OrderBy(item => item.Position).ToList();
                var gaps = ordered.Where(item => item.Role == OutfitItemRole.Gap).ToList();
                var anchor = ordered.FirstOrDefault(item => item.Role == OutfitItemRole.Anchor && item.ProductId is not null);
                decimal? completion = gaps.Count == 0 ? null : gaps.Sum(item => item.Price ?? 0);
                var anchorPrice = anchor?.Price;
                decimal? total = completion is null && anchorPrice is null ? null : (completion ?? 0) + (anchorPrice ?? 0);
                var weather = ReadWeather(outfit.WeatherJson);

                return new OutfitResponse(
                    outfit.Id,
                    outfit.Kind,
                    outfit.Status,
                    outfit.Completeness,
                    outfit.Occasion,
                    outfit.ForDate,
                    outfit.Seed,
                    outfit.Score,
                    outfit.Rule,
                    outfit.PersonaId,
                    weather is null ? null : OutfitWeatherResponse.From(weather),
                    [.. ordered.Select(Piece)],
                    completion,
                    anchorPrice,
                    total,
                    new OutfitNarrationResponse(outfit.Narration, outfit.NarrationProvider),
                    outfit.CreatedAt,
                    outfit.SavedAt,
                    outfit.WornAt);
            }),
        ];
    }
}
