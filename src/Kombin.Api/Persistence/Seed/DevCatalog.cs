using System.Text.Json;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Catalog.UpsertOffers;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Persistence.Seed;

/// <summary>The hand-written development catalog (<c>dev-catalog.json</c>, an embedded resource) and its translation into upsert items.</summary>
internal sealed record DevCatalog(
    IReadOnlyDictionary<string, string> Marketplaces,
    IReadOnlyDictionary<string, string> BrandStores,
    IReadOnlyDictionary<string, string[]> SizeSets,
    IReadOnlyList<DevProduct> Products)
{
    /// <summary>The <see cref="DevOffer.Seller"/> value that means "the product's brand store".</summary>
    public const string BrandStoreSeller = "store";

    private const string ResourceName = "Kombin.Api.Persistence.Seed.dev-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static DevCatalog Load()
    {
        using var stream = typeof(DevCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

        return JsonSerializer.Deserialize<DevCatalog>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"'{ResourceName}' is empty.");
    }

    /// <summary>One listing per offer, in file order, with every derived field (ids, URLs, titles, images, sizes) filled in.</summary>
    public IReadOnlyList<SeedListing> Listings() =>
        [.. Products.SelectMany((product, productIndex) => product.Offers.Select(offer => ToListing(product, productIndex, offer)))];

    public static string SellerSlug(DevProduct product, DevOffer offer) =>
        offer.Seller == BrandStoreSeller ? Slugs.Slugify(product.Brand) + "-store" : offer.Seller;

    private SeedListing ToListing(DevProduct product, int productIndex, DevOffer offer)
    {
        var sellerSlug = SellerSlug(product, offer);
        var code = product.ModelCode.ToLowerInvariant();
        var productSlug = Slugs.Slugify(product.Name);
        var imageSeed = Slugs.Slugify($"{product.Brand} {product.Name}");

        var url = offer.Seller switch
        {
            "trendyol" => $"{Marketplaces[offer.Seller]}/{Slugs.Slugify(product.Brand)}/{productSlug}-p-{code}",
            "hepsiburada" => $"{Marketplaces[offer.Seller]}/{productSlug}-pm-{code}",
            BrandStoreSeller => $"{BrandStores[product.Brand]}/p/{productSlug}-{code}",
            _ => throw new InvalidOperationException($"Unknown seller '{offer.Seller}' on {product.ModelCode}."),
        };

        var item = new UpsertOfferItem(
            ExternalId: $"{sellerSlug}-{code}",
            Url: url,
            Title: $"{product.Brand} {product.Name}",
            Price: offer.Price,
            OriginalPrice: offer.OriginalPrice,
            Currency: null,
            ShippingCost: offer.ShippingCost,
            FreeShippingThreshold: offer.FreeShippingThreshold,
            StockStatus: offer.Stock,
            AvailableSizes: AvailableSizes(SizeSets[product.SizeSet], offer.Stock),
            ImageUrls: [.. Enumerable.Range(1, product.Images).Select(number => $"https://picsum.photos/seed/{imageSeed}-{number}/600/800")],
            Product: new UpsertProductInfo(
                BrandName: product.Brand,
                Name: product.Name,
                ModelCode: product.ModelCode,
                CategorySlug: product.Category,
                Audience: product.Audience,
                PrimaryColor: product.Color,
                Pattern: product.Pattern,
                Fit: product.Fit,
                Formality: product.Formality,
                Seasons: product.Seasons,
                Material: product.Material,
                Description: product.Description,
                CanonicalProductId: null));

        return new SeedListing(productIndex, product, offer, sellerSlug, item);
    }

    /// <summary>Everything in stock; a low-stock listing keeps only the two middle sizes; an out-of-stock one has none.</summary>
    private static string[] AvailableSizes(string[] sizes, string stock) =>
        Enum.Parse<StockStatus>(stock, ignoreCase: true) switch
        {
            StockStatus.OutOfStock => [],
            StockStatus.LowStock => [.. sizes.Skip(Math.Max(0, (sizes.Length - 2) / 2)).Take(Math.Min(2, sizes.Length))],
            _ => sizes,
        };
}

internal sealed record DevProduct(
    string Brand,
    string Name,
    string ModelCode,
    string Category,
    string Audience,
    string Color,
    string Pattern,
    string Fit,
    int Formality,
    string[] Seasons,
    string Material,
    string Description,
    string SizeSet,
    int Images,
    IReadOnlyList<DevOffer> Offers)
{
    /// <summary>True when at least one offer can be bought; products without one are hidden from the product list.</summary>
    public bool IsSellable => Offers.Any(offer => offer.Stock is not nameof(StockStatus.OutOfStock));
}

internal sealed record DevOffer(
    string Seller,
    decimal Price,
    decimal? OriginalPrice = null,
    decimal? ShippingCost = null,
    decimal? FreeShippingThreshold = null,
    string Stock = nameof(StockStatus.InStock));

/// <summary>A seed offer with the position of its product in the file, the seller it lands on and the item the upserter receives.</summary>
internal sealed record SeedListing(int ProductIndex, DevProduct Product, DevOffer Offer, string SellerSlug, UpsertOfferItem Item);
