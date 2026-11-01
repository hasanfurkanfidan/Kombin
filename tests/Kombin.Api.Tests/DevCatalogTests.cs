using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Features.Catalog.UpsertOffers;
using Kombin.Api.Persistence.Configurations.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Seed;

namespace Kombin.Api.Tests;

/// <summary>Guards the hand-written seed file: a typo there would otherwise only surface when a developer starts the API.</summary>
public class DevCatalogTests
{
    private static readonly DevCatalog Catalog = DevCatalog.Load();

    private static readonly string[] RequestedBrands =
    [
        "Mavi", "Koton", "LC Waikiki", "Zara", "Defacto", "Boyner", "Derimod", "Nike", "Adidas", "Beymen Club", "Jack & Jones", "Pull&Bear", "Vakko",
    ];

    private static readonly string[] LeafCategories =
    [
        .. CatalogSeed.Categories().Where(category => category.ParentId is not null).Select(category => category.Slug),
    ];

    [Fact]
    public void HasFortyToFiftyProductsWithUniqueModelCodes()
    {
        Assert.InRange(Catalog.Products.Count, 40, 50);
        Assert.Equal(Catalog.Products.Count, Catalog.Products.Select(product => product.ModelCode).Distinct().Count());
    }

    [Fact]
    public void EveryLeafCategoryHasAtLeastTwoProductsAndNoProductSitsOnAParent()
    {
        var perCategory = Catalog.Products.GroupBy(product => product.Category).ToDictionary(group => group.Key, group => group.Count());

        Assert.All(LeafCategories, leaf => Assert.True(perCategory.GetValueOrDefault(leaf) >= 2, $"{leaf} has {perCategory.GetValueOrDefault(leaf)} products"));
        Assert.All(perCategory.Keys, category => Assert.Contains(category, LeafCategories));
    }

    [Fact]
    public void EveryRequestedBrandIsUsedAndNoOtherIs()
    {
        Assert.Equal(RequestedBrands.Order(), Catalog.Products.Select(product => product.Brand).Distinct().Order());
        Assert.All(RequestedBrands, brand => Assert.True(Catalog.BrandStores.ContainsKey(brand), brand));
    }

    [Fact]
    public void AudiencesAreMixed()
    {
        Assert.Equal(["Men", "Unisex", "Women"], Catalog.Products.Select(product => product.Audience).Distinct().Order());
        Assert.True(Catalog.Products.Select(product => product.Color).Distinct().Count() >= 8);
        Assert.True(Catalog.Products.Select(product => product.Pattern).Distinct().Count() >= 4);
        Assert.True(Catalog.Products.Select(product => product.Fit).Distinct().Count() >= 4);
        Assert.Equal([1, 2, 3, 4, 5], Catalog.Products.Select(product => product.Formality).Distinct().Order());
    }

    [Fact]
    public void SeasonsMakeSenseForTheItem()
    {
        var parka = Catalog.Products.Single(product => product.Name.Contains("Parka", StringComparison.Ordinal));
        var linenShirt = Catalog.Products.Single(product => product.Name.Contains("Keten Karışımlı", StringComparison.Ordinal) && product.Category == "ust-giyim/gomlek");

        Assert.Equal(["Autumn", "Winter"], parka.Seasons);
        Assert.Equal(["Spring", "Summer"], linenShirt.Seasons);
        Assert.All(Catalog.Products.Where(product => product.Category is "dis-giyim/mont" or "dis-giyim/kaban"), product => Assert.DoesNotContain("Summer", product.Seasons));
    }

    [Fact]
    public void PricesStayInARealisticRangePerCategory()
    {
        (string Category, decimal Min, decimal Max)[] ranges =
        [
            ("ust-giyim/tisort", 300, 900),
            ("alt-giyim/jean", 1000, 2500),
            ("dis-giyim/mont", 1200, 7000),
            ("dis-giyim/kaban", 2500, 7000),
            ("ayakkabi/sneaker", 2000, 6000),
            ("ayakkabi/loafer", 1500, 4000),
            ("aksesuar/canta", 800, 5000),
        ];

        foreach (var (category, min, max) in ranges)
        {
            var prices = Catalog.Products.Where(product => product.Category == category).SelectMany(product => product.Offers.Select(offer => offer.Price));
            Assert.All(prices, price => Assert.InRange(price, min, max));
        }

        Assert.All(Catalog.Products.SelectMany(product => product.Offers), offer => Assert.True(offer.Price > 0m));
    }

    [Fact]
    public void EveryProductIsOnTrendyolAndTheOtherSellersAreRoughlyAsOftenAsRequested()
    {
        var products = Catalog.Products;

        Assert.All(products, product => Assert.Contains(product.Offers, offer => offer.Seller == "trendyol"));
        Assert.All(products, product => Assert.Equal(product.Offers.Count, product.Offers.Select(offer => offer.Seller).Distinct().Count()));
        Assert.InRange(products.Count(product => product.Offers.Any(offer => offer.Seller == "hepsiburada")) / (double)products.Count, 0.5, 0.7);
        Assert.InRange(products.Count(product => product.Offers.Any(offer => offer.Seller == DevCatalog.BrandStoreSeller)) / (double)products.Count, 0.3, 0.5);
        Assert.Contains(products, product => product.Offers.Count == 1);
        Assert.Contains(products, product => product.Offers.Count == 3);
        Assert.All(products, product => Assert.InRange(product.Offers.Count, 1, 3));
    }

    [Fact]
    public void AboutAThirdOfTheOffersCarryAnOriginalPriceFifteenToFortyPercentHigher()
    {
        var offers = Catalog.Products.SelectMany(product => product.Offers).ToList();
        var discounted = offers.Where(offer => offer.OriginalPrice is not null).ToList();

        Assert.InRange(discounted.Count / (double)offers.Count, 0.28, 0.40);
        Assert.All(discounted, offer => Assert.InRange(offer.OriginalPrice!.Value / offer.Price, 1.15m, 1.40m));
    }

    [Fact]
    public void OffersOfOneProductHaveDifferentEffectivePrices()
    {
        foreach (var product in Catalog.Products)
        {
            var effective = product.Offers.Select(offer => GetProductEndpoint.EffectivePrice(offer.Price, offer.ShippingCost, offer.FreeShippingThreshold)).ToList();

            Assert.True(effective.Distinct().Count() == effective.Count, $"{product.ModelCode}: {string.Join(", ", effective)}");
        }
    }

    [Fact]
    public void ShippingFollowsTheSellerConventions()
    {
        var trendyol = Catalog.Products.SelectMany(product => product.Offers).Where(offer => offer.Seller == "trendyol").ToList();
        var stores = Catalog.Products.SelectMany(product => product.Offers).Where(offer => offer.Seller == DevCatalog.BrandStoreSeller).ToList();

        Assert.True(trendyol.Count(offer => offer is { ShippingCost: 39.90m, FreeShippingThreshold: 2000m }) > trendyol.Count / 2);
        Assert.True(stores.Count(offer => offer.ShippingCost == 0m) > stores.Count / 2);
        Assert.Contains(Catalog.Products.SelectMany(product => product.Offers), offer => offer.ShippingCost is null);
    }

    [Fact]
    public void SomeOffersAreLowOrOutOfStockAndTwoOrThreeProductsAreOutOfStockEverywhere()
    {
        var offers = Catalog.Products.SelectMany(product => product.Offers).ToList();

        Assert.Contains(offers, offer => offer.Stock == nameof(StockStatus.LowStock));
        Assert.Contains(offers, offer => offer.Stock == nameof(StockStatus.OutOfStock));
        Assert.InRange(Catalog.Products.Count(product => !product.IsSellable), 2, 3);
        Assert.Contains(Catalog.Products, product => product.IsSellable && product.Offers.Any(offer => offer.Stock == nameof(StockStatus.OutOfStock)));
    }

    [Fact]
    public void EveryListingPassesTheSameValidationAsTheEndpoint()
    {
        foreach (var group in Catalog.Listings().GroupBy(listing => listing.SellerSlug))
        {
            var result = new UpsertOffersValidator().Validate(new UpsertOffersRequest(group.Key, [.. group.Select(listing => listing.Item)]));

            Assert.True(result.IsValid, $"{group.Key}: {string.Join("; ", result.Errors.Select(error => error.ErrorMessage))}");
        }
    }

    [Fact]
    public void ListingsHaveUniqueExternalIdsPerSellerAndStableImages()
    {
        var listings = Catalog.Listings();

        Assert.Equal(listings.Count, listings.Select(listing => (listing.SellerSlug, listing.Item.ExternalId)).Distinct().Count());
        Assert.All(listings, listing => Assert.InRange(listing.Item.ImageUrls!.Count, 1, 3));
        Assert.All(listings, listing => Assert.All(listing.Item.ImageUrls!, url => Assert.StartsWith("https://picsum.photos/seed/", url, StringComparison.Ordinal)));
        Assert.All(listings, listing => Assert.EndsWith("/600/800", listing.Item.ImageUrls![0], StringComparison.Ordinal));

        // Offers of one product must share the exact image URLs, so the product's gallery is the same whichever seller created it.
        Assert.All(listings.GroupBy(listing => listing.ProductIndex), group => Assert.Single(group.Select(listing => string.Join('|', listing.Item.ImageUrls!)).Distinct()));
    }

    [Fact]
    public void SizesMatchTheCategory()
    {
        var listings = Catalog.Listings();

        Assert.All(listings.Where(listing => listing.Product.Category == "alt-giyim/jean" && listing.Offer.Stock == "InStock"),
            listing => Assert.Equal(["28", "30", "32", "34", "36"], listing.Item.AvailableSizes));
        Assert.All(listings.Where(listing => listing.Product.Category.StartsWith("ayakkabi/", StringComparison.Ordinal) && listing.Offer.Stock == "InStock"),
            listing => Assert.All(listing.Item.AvailableSizes!, size => Assert.InRange(int.Parse(size, System.Globalization.CultureInfo.InvariantCulture), 36, 45)));
        Assert.All(listings.Where(listing => listing.Offer.Stock == "OutOfStock"), listing => Assert.Empty(listing.Item.AvailableSizes!));
        Assert.All(listings.Where(listing => listing.Offer.Stock == "LowStock"), listing => Assert.InRange(listing.Item.AvailableSizes!.Count, 1, 2));
    }

    [Fact]
    public void BrandStoresUseTheBrandSlug()
    {
        var storeSlugs = Catalog.Listings().Where(listing => listing.Offer.Seller == DevCatalog.BrandStoreSeller).Select(listing => listing.SellerSlug).Distinct().Order().ToList();

        Assert.All(storeSlugs, slug => Assert.EndsWith("-store", slug, StringComparison.Ordinal));
        Assert.Contains("jack-jones-store", storeSlugs);
        Assert.DoesNotContain(storeSlugs, slug => slug.Contains("--", StringComparison.Ordinal));
    }
}
