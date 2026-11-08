using System.Globalization;
using System.Net;
using System.Text.Json;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class CatalogReadTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Names(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];

    [Fact]
    public async Task CategoriesAreATreeSortedBySortOrder()
    {
        using var client = factory.CreateClient();

        var tree = await client.GetJsonAsync("/api/v1/catalog/categories", Ct);

        var roots = tree.EnumerateArray().ToList();
        Assert.Equal(["ust-giyim", "alt-giyim", "dis-giyim", "ayakkabi", "aksesuar"], roots.Select(root => root.GetProperty("slug").GetString()));
        Assert.Equal("Üst Giyim", roots[0].GetProperty("name").GetString());
        Assert.Equal(
            ["ust-giyim/tisort", "ust-giyim/gomlek", "ust-giyim/triko", "ust-giyim/blazer"],
            roots[0].GetProperty("children").EnumerateArray().Select(child => child.GetProperty("slug").GetString()));
        Assert.All(roots[0].GetProperty("children").EnumerateArray(), child => Assert.Equal(0, child.GetProperty("children").GetArrayLength()));
    }

    [Fact]
    public async Task SeededSellersExist()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

        var sellers = await db.Sellers.Where(seller => seller.Kind == SellerKind.Marketplace).Select(seller => seller.Slug).ToListAsync(Ct);

        Assert.Contains("trendyol", sellers);
        Assert.Contains("hepsiburada", sellers);
    }

    [Fact]
    public async Task BrandsAreSearchableByCaseInsensitiveContainsAndPaged()
    {
        using var client = factory.CreateClient();
        var token = Unique("Zq");
        await client.UpsertOkAsync("trendyol",
        [
            Item($"b1-{Guid.NewGuid():N}", 10m, $"{token} Alpha", "Thing"),
            Item($"b2-{Guid.NewGuid():N}", 10m, $"The {token} Beta", "Thing"),
            Item($"b3-{Guid.NewGuid():N}", 10m, $"{token} Gamma", "Thing"),
        ], Ct);

        var all = await client.GetJsonAsync($"/api/v1/catalog/brands?query={token.ToUpperInvariant()}", Ct);
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());

        // Names that start with the term come first.
        var names = all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToList();
        Assert.Equal($"The {token} Beta", names[2]);

        var paged = await client.GetJsonAsync($"/api/v1/catalog/brands?query={token}&page=2&pageSize=2", Ct);
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        Assert.Equal(2, paged.GetProperty("page").GetInt32());
        Assert.Equal(2, paged.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    public async Task BrandSearchTreatsWildcardsLiterally()
    {
        using var client = factory.CreateClient();

        var result = await client.GetJsonAsync("/api/v1/catalog/brands?query=%25", Ct);

        Assert.Equal(0, result.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task CategoryFilterOnAParentIncludesItsChildren()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Cat");
        await client.UpsertOkAsync("trendyol",
        [
            Item($"c1-{Guid.NewGuid():N}", 10m, brand, "Blazer Item", category: "ust-giyim/blazer"),
            Item($"c2-{Guid.NewGuid():N}", 10m, brand, "Tisort Item", category: "ust-giyim/tisort"),
            Item($"c3-{Guid.NewGuid():N}", 10m, brand, "Jean Item", category: "alt-giyim/jean"),
        ], Ct);
        var baseUrl = $"/api/v1/catalog/products?brandSlug={Slug(brand)}&sort=priceAsc";

        var parent = await client.GetJsonAsync($"{baseUrl}&categorySlug=ust-giyim", Ct);
        Assert.Equal(["Blazer Item", "Tisort Item"], Names(parent).Order());

        var child = await client.GetJsonAsync($"{baseUrl}&categorySlug=ust-giyim/blazer", Ct);
        Assert.Equal(["Blazer Item"], Names(child));

        var other = await client.GetJsonAsync($"{baseUrl}&categorySlug=alt-giyim", Ct);
        Assert.Equal(["Jean Item"], Names(other));

        var empty = await client.GetJsonAsync($"{baseUrl}&categorySlug=dis-giyim", Ct);
        Assert.Equal(0, empty.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task InvalidEnumFiltersAreValidationErrors()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/catalog/products?sort=cheapest&color=plaid&audience=aliens", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("sort", out _));
        Assert.True(errors.TryGetProperty("color", out _));
        Assert.True(errors.TryGetProperty("audience", out _));
    }

    [Fact]
    public async Task UnknownCategoryOrBrandFiltersAreNotFound()
    {
        using var client = factory.CreateClient();

        using var category = await client.GetAsync("/api/v1/catalog/products?categorySlug=no-such", Ct);
        Assert.Equal(HttpStatusCode.NotFound, category.StatusCode);
        Assert.Equal("Catalog.CategoryNotFound", (await category.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var brand = await client.GetAsync("/api/v1/catalog/products?brandSlug=no-such", Ct);
        Assert.Equal(HttpStatusCode.NotFound, brand.StatusCode);
        Assert.Equal("Catalog.BrandNotFound", (await brand.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task PriceFiltersApplyToTheLowestOfferPrice()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Prc");
        var cheap = $"cheap-{Guid.NewGuid():N}";

        // One product with offers at 100 and 300, another at 200 only.
        await client.UpsertOkAsync("trendyol",
        [
            Item(cheap, 100m, brand, "Two Offers", modelCode: "P-1"),
            Item($"mid-{Guid.NewGuid():N}", 200m, brand, "One Offer", modelCode: "P-2"),
        ], Ct);
        await client.UpsertOkAsync("hepsiburada", [Item($"dear-{Guid.NewGuid():N}", 300m, brand, "Two Offers", modelCode: "P-1")], Ct);

        var baseUrl = $"/api/v1/catalog/products?brandSlug={Slug(brand)}&sort=priceAsc";

        // Two Offers has an offer at 300 but its lowest is 100, so min=150 must drop it.
        Assert.Equal(["One Offer"], Names(await client.GetJsonAsync($"{baseUrl}&minPrice=150", Ct)));
        Assert.Equal(["Two Offers"], Names(await client.GetJsonAsync($"{baseUrl}&maxPrice=150", Ct)));
        Assert.Equal(["Two Offers", "One Offer"], Names(await client.GetJsonAsync($"{baseUrl}&minPrice=100&maxPrice=200", Ct)));
        Assert.Equal(["One Offer"], Names(await client.GetJsonAsync($"{baseUrl}&minPrice=200&maxPrice=250", Ct)));

        var list = await client.GetJsonAsync(baseUrl, Ct);
        var twoOffers = list.GetProperty("items")[0];
        Assert.Equal(100m, twoOffers.GetProperty("lowestPrice").GetDecimal());
        Assert.Equal(2, twoOffers.GetProperty("offerCount").GetInt32());
    }

    [Fact]
    public async Task ListSortsByPriceAndFiltersByColorAudienceAndText()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Srt");
        await client.UpsertOkAsync("trendyol",
        [
            Item($"s1-{Guid.NewGuid():N}", 300m, brand, "Red Dress", color: "Red", audience: "Women", category: "alt-giyim/etek"),
            Item($"s2-{Guid.NewGuid():N}", 100m, brand, "Blue Jean", color: "Blue", audience: "Men", category: "alt-giyim/jean"),
            Item($"s3-{Guid.NewGuid():N}", 200m, brand, "Green Skirt", color: "Green", audience: "Women", category: "alt-giyim/etek"),
        ], Ct);
        var baseUrl = $"/api/v1/catalog/products?brandSlug={Slug(brand)}";

        Assert.Equal(["Blue Jean", "Green Skirt", "Red Dress"], Names(await client.GetJsonAsync($"{baseUrl}&sort=priceAsc", Ct)));
        Assert.Equal(["Red Dress", "Green Skirt", "Blue Jean"], Names(await client.GetJsonAsync($"{baseUrl}&sort=priceDesc", Ct)));

        // Newest first: the last one inserted comes first, for both "relevance" (default) and "newest".
        Assert.Equal(["Green Skirt", "Blue Jean", "Red Dress"], Names(await client.GetJsonAsync($"{baseUrl}&sort=newest", Ct)));
        Assert.Equal(["Green Skirt", "Blue Jean", "Red Dress"], Names(await client.GetJsonAsync(baseUrl, Ct)));

        Assert.Equal(["Blue Jean"], Names(await client.GetJsonAsync($"{baseUrl}&color=blue", Ct)));
        Assert.Equal(["Blue Jean"], Names(await client.GetJsonAsync($"{baseUrl}&audience=Men", Ct)));
        Assert.Equal(["Green Skirt", "Red Dress"], Names(await client.GetJsonAsync($"{baseUrl}&audience=Women&sort=priceAsc", Ct)));
        Assert.Equal(["Green Skirt"], Names(await client.GetJsonAsync($"{baseUrl}&query=green", Ct)));
        Assert.Equal(3, (await client.GetJsonAsync($"/api/v1/catalog/products?query={Slug(brand).ToUpperInvariant()}", Ct)).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ListItemsCarryTheLowestOffersDiscount()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Dsc");
        await client.UpsertOkAsync("trendyol", [Item($"d1-{Guid.NewGuid():N}", 150m, brand, "On Sale", originalPrice: 200m)], Ct);

        var item = (await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}", Ct)).GetProperty("items")[0];

        Assert.Equal(150m, item.GetProperty("lowestPrice").GetDecimal());
        Assert.Equal(200m, item.GetProperty("lowestOriginalPrice").GetDecimal());
        Assert.Equal(25, item.GetProperty("discountPercent").GetInt32());
        Assert.Equal(Slug(brand), item.GetProperty("brand").GetProperty("slug").GetString());
        Assert.Equal("ust-giyim/blazer", item.GetProperty("category").GetProperty("slug").GetString());
        Assert.Equal("Green", item.GetProperty("primaryColor").GetString());
        Assert.Equal("Trendyol", item.GetProperty("cheapestSeller").GetProperty("name").GetString());
        Assert.Equal("trendyol", item.GetProperty("cheapestSeller").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task ListItemsNameTheSellerOfTheOfferThatDefinesTheLowestPrice()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Chp");

        // Trendyol asks 300, Hepsiburada 100 for the same product: the cheapest seller is Hepsiburada, whatever the order of arrival.
        await client.UpsertOkAsync("trendyol", [Item($"c1-{Guid.NewGuid():N}", 300m, brand, "Shared Coat", modelCode: "CH-1")], Ct);
        await client.UpsertOkAsync("hepsiburada", [Item($"c2-{Guid.NewGuid():N}", 100m, brand, "Shared Coat", modelCode: "CH-1")], Ct);
        var item = (await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}", Ct)).GetProperty("items")[0];

        Assert.Equal(100m, item.GetProperty("lowestPrice").GetDecimal());
        Assert.Equal(2, item.GetProperty("offerCount").GetInt32());
        Assert.Equal("Hepsiburada", item.GetProperty("cheapestSeller").GetProperty("name").GetString());
        Assert.Equal("hepsiburada", item.GetProperty("cheapestSeller").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task TheCheapestSellerIgnoresOffersThatCannotBeBought()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Chs");

        // The cheaper listing is sold out, so the seller of the cheapest offer one can buy is named.
        await client.UpsertOkAsync("trendyol", [Item($"s1-{Guid.NewGuid():N}", 50m, brand, "Odd Coat", modelCode: "CS-1", stockStatus: "OutOfStock")], Ct);
        await client.UpsertOkAsync("hepsiburada", [Item($"s2-{Guid.NewGuid():N}", 200m, brand, "Odd Coat", modelCode: "CS-1")], Ct);
        var item = (await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}", Ct)).GetProperty("items")[0];

        Assert.Equal(200m, item.GetProperty("lowestPrice").GetDecimal());
        Assert.Equal("hepsiburada", item.GetProperty("cheapestSeller").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task ProductsWithOnlyOutOfStockOffersAreHiddenFromTheListButStillHaveADetailPage()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Oos");
        await client.UpsertOkAsync("trendyol",
        [
            Item($"o1-{Guid.NewGuid():N}", 100m, brand, "Sold Out", stockStatus: "OutOfStock"),
            Item($"o2-{Guid.NewGuid():N}", 100m, brand, "Available", stockStatus: "LowStock"),
        ], Ct);

        var list = await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}", Ct);
        Assert.Equal(["Available"], Names(list));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        var soldOut = await db.Products.SingleAsync(product => product.Name == "Sold Out" && product.Brand.Slug == Slug(brand), Ct);

        var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{soldOut.Id}", Ct);
        Assert.Equal("Sold Out", detail.GetProperty("name").GetString());
        var offer = Assert.Single(detail.GetProperty("offers").EnumerateArray());
        Assert.Equal("OutOfStock", offer.GetProperty("stockStatus").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("priceSummary").GetProperty("lowestNow").ValueKind);
        Assert.False(detail.GetProperty("priceSummary").GetProperty("isLowestOfLast90Days").GetBoolean());
    }

    [Fact]
    public async Task ProductDetailCarriesAttributesImagesAndOffers()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Det");
        var images = new[] { "https://img.example.com/a.jpg", "https://img.example.com/b.jpg" };
        await client.UpsertOkAsync("trendyol", [Item($"x-{Guid.NewGuid():N}", 250m, brand, "Detail Coat", modelCode: "dc-1", category: "dis-giyim/kaban", imageUrls: images, color: "Navy")], Ct);

        var productId = await client.FindProductIdAsync(Slug(brand), Ct);
        var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}", Ct);

        Assert.Equal("Detail Coat", detail.GetProperty("name").GetString());
        Assert.Equal("DC-1", detail.GetProperty("modelCode").GetString());
        Assert.Equal("Navy", detail.GetProperty("primaryColor").GetString());
        Assert.Equal("Women", detail.GetProperty("audience").GetString());
        Assert.Equal("Unknown", detail.GetProperty("fit").GetString());
        Assert.Equal(["Spring", "Summer", "Autumn", "Winter"], detail.GetProperty("seasons").EnumerateArray().Select(season => season.GetString()));
        Assert.Equal("dis-giyim/kaban", detail.GetProperty("category").GetProperty("slug").GetString());
        Assert.Equal("Kaban", detail.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal(images, detail.GetProperty("images").EnumerateArray().Select(image => image.GetString()));
        Assert.Equal(images[0], detail.GetProperty("primaryImageUrl").GetString());

        var offer = detail.GetProperty("offers")[0];
        Assert.Equal(250m, offer.GetProperty("currentPrice").GetDecimal());
        Assert.Equal("TRY", offer.GetProperty("currency").GetString());
        Assert.Equal(["S", "M"], offer.GetProperty("availableSizes").EnumerateArray().Select(size => size.GetString()));

        // The seller's own URL never reaches the app: every offer is opened through the click-recording redirect.
        Assert.False(offer.TryGetProperty("url", out _));
        Assert.Equal($"/api/v1/go/{offer.GetProperty("id").GetGuid()}", offer.GetProperty("goPath").GetString());
    }

    [Fact]
    public async Task UnknownProductIsNotFound()
    {
        using var client = factory.CreateClient();

        using var detail = await client.GetAsync($"/api/v1/catalog/products/{Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal("Catalog.ProductNotFound", (await detail.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var history = await client.GetAsync($"/api/v1/catalog/products/{Guid.NewGuid()}/price-history", Ct);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        Assert.Equal("Catalog.ProductNotFound", (await history.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task PriceHistoryReturnsDailyMinimumsAndTheSummaryFlagsTheNinetyDayLow()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Hst");
        var externalId = $"h-{Guid.NewGuid():N}";
        await client.UpsertOkAsync("trendyol", [Item(externalId, 100m, brand, "History Jacket", category: "dis-giyim/ceket")], Ct);
        var productId = await client.FindProductIdAsync(Slug(brand), Ct);

        // Back-fill the series: 130 ten days ago, and on the day five days ago two observations of which 90 is the cheaper.
        var today = DateTimeOffset.UtcNow.UtcDateTime.Date;
        Guid offerId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            offerId = await db.Offers.Where(offer => offer.ProductId == productId).Select(offer => offer.Id).SingleAsync(Ct);

            db.PriceSnapshots.AddRange(
                Snapshot(offerId, 130m, new DateTimeOffset(today.AddDays(-10).AddHours(10), TimeSpan.Zero)),
                Snapshot(offerId, 95m, new DateTimeOffset(today.AddDays(-5).AddHours(9), TimeSpan.Zero)),
                Snapshot(offerId, 90m, new DateTimeOffset(today.AddDays(-5).AddHours(15), TimeSpan.Zero)));
            await db.SaveChangesAsync(Ct);
        }

        var history = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}/price-history", Ct);
        var series = Assert.Single(history.EnumerateArray());
        Assert.Equal(offerId, series.GetProperty("offerId").GetGuid());
        Assert.Equal("trendyol", series.GetProperty("seller").GetProperty("slug").GetString());
        Assert.Equal("TR", series.GetProperty("seller").GetProperty("country").GetString());

        var points = series.GetProperty("points").EnumerateArray().Select(point => (point.GetProperty("date").GetString()!, point.GetProperty("minPrice").GetDecimal())).ToList();
        Assert.Equal(
            [
                (Day(today.AddDays(-10)), 130m),
                (Day(today.AddDays(-5)), 90m),
                (Day(today), 100m),
            ],
            points);

        // A short window drops the oldest point; out-of-range windows are clamped to 7..365 instead of rejected.
        var week = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}/price-history?days=7", Ct);
        Assert.Equal(2, week[0].GetProperty("points").GetArrayLength());
        var clampedLow = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}/price-history?days=1", Ct);
        Assert.Equal(2, clampedLow[0].GetProperty("points").GetArrayLength());
        var clampedHigh = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}/price-history?days=100000", Ct);
        Assert.Equal(3, clampedHigh[0].GetProperty("points").GetArrayLength());

        // Now 100, but it was 90 five days ago: not the low.
        var summary = (await client.GetJsonAsync($"/api/v1/catalog/products/{productId}", Ct)).GetProperty("priceSummary");
        Assert.Equal(100m, summary.GetProperty("lowestNow").GetDecimal());
        Assert.Equal(90m, summary.GetProperty("lowestLast90Days").GetDecimal());
        Assert.Equal(130m, summary.GetProperty("highestLast90Days").GetDecimal());
        Assert.False(summary.GetProperty("isLowestOfLast90Days").GetBoolean());

        // Drop below the old low: today's point becomes the day's minimum and the summary flips.
        await client.UpsertOkAsync("trendyol", [Item(externalId, 80m, brand, "History Jacket", category: "dis-giyim/ceket")], Ct);

        var after = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}/price-history", Ct);
        var latest = after[0].GetProperty("points").EnumerateArray().Last();
        Assert.Equal(80m, latest.GetProperty("minPrice").GetDecimal());
        Assert.Equal("InStock", latest.GetProperty("stockStatus").GetString());

        var summaryAfter = (await client.GetJsonAsync($"/api/v1/catalog/products/{productId}", Ct)).GetProperty("priceSummary");
        Assert.Equal(80m, summaryAfter.GetProperty("lowestNow").GetDecimal());
        Assert.Equal(80m, summaryAfter.GetProperty("lowestLast90Days").GetDecimal());
        Assert.True(summaryAfter.GetProperty("isLowestOfLast90Days").GetBoolean());
    }

    [Fact]
    public async Task SnapshotsOlderThanTheWindowDoNotCountTowardsTheSummary()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Old");
        await client.UpsertOkAsync("trendyol", [Item($"old-{Guid.NewGuid():N}", 100m, brand, "Old Low", category: "dis-giyim/mont")], Ct);
        var productId = await client.FindProductIdAsync(Slug(brand), Ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            var offerId = await db.Offers.Where(offer => offer.ProductId == productId).Select(offer => offer.Id).SingleAsync(Ct);
            db.PriceSnapshots.Add(Snapshot(offerId, 10m, DateTimeOffset.UtcNow.AddDays(-200)));
            await db.SaveChangesAsync(Ct);
        }

        var summary = (await client.GetJsonAsync($"/api/v1/catalog/products/{productId}", Ct)).GetProperty("priceSummary");

        Assert.Equal(100m, summary.GetProperty("lowestLast90Days").GetDecimal());
        Assert.True(summary.GetProperty("isLowestOfLast90Days").GetBoolean());
    }

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static PriceSnapshot Snapshot(Guid offerId, decimal price, DateTimeOffset observedAt) =>
        new() { OfferId = offerId, Price = price, StockStatus = StockStatus.InStock, ObservedAt = observedAt };
}
