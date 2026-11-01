using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class CatalogIngestionTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpsertCreatesTheWholeGraphThenOnlyTouchesLastSeenUntilThePriceChanges()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Mavi");
        var sellerSlug = $"{Slug(brand)}-store";
        var externalId = $"ext-{Guid.NewGuid():N}";

        // 1. First sight: brand, brand-store seller, product, offer, image and the first snapshot are created.
        var first = await client.UpsertOkAsync(sellerSlug, [Item(externalId, 1299.90m, brand, "Wool Blend Blazer", modelCode: "mv-100")], Ct);

        Assert.Equal(1, first.GetProperty("created").GetInt32());
        Assert.Equal(0, first.GetProperty("updated").GetInt32());
        Assert.Equal(1, first.GetProperty("snapshotsWritten").GetInt32());
        Assert.Equal(0, first.GetProperty("errors").GetArrayLength());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

            var storedBrand = await db.Brands.SingleAsync(candidate => candidate.Slug == Slug(brand), Ct);
            Assert.Equal(brand, storedBrand.Name);

            var seller = await db.Sellers.SingleAsync(candidate => candidate.Slug == sellerSlug, Ct);
            Assert.Equal(SellerKind.BrandStore, seller.Kind);
            Assert.Equal("https://shop.example.com", seller.WebsiteUrl);

            var product = await db.Products.SingleAsync(candidate => candidate.BrandId == storedBrand.Id, Ct);
            Assert.Equal("wool-blend-blazer", product.Slug);
            Assert.Equal("MV-100", product.ModelCode);
            Assert.True(product.IsActive);
            Assert.Equal($"https://img.example.com/{externalId}.jpg", product.PrimaryImageUrl);
            Assert.Equal(3, product.Formality);
            Assert.Equal(Season.AllYear, product.Seasons);

            var image = await db.ProductImages.SingleAsync(candidate => candidate.ProductId == product.Id, Ct);
            Assert.Equal(product.PrimaryImageUrl, image.Url);

            var offer = await db.Offers.SingleAsync(candidate => candidate.SellerId == seller.Id, Ct);
            Assert.Equal(externalId, offer.ExternalId);
            Assert.Equal(1299.90m, offer.CurrentPrice);
            Assert.Equal("TRY", offer.Currency);
            Assert.Equal(["S", "M"], offer.AvailableSizes);
            Assert.True(offer.IsActive);

            Assert.Equal(1, await db.PriceSnapshots.CountAsync(snapshot => snapshot.OfferId == offer.Id, Ct));
        }

        var firstSeenAt = await LastSeenAtAsync(sellerSlug, externalId);

        // 2. Identical re-observation: the offer is updated (LastSeenAt moves) but no snapshot is written.
        var second = await client.UpsertOkAsync(sellerSlug, [Item(externalId, 1299.90m, brand, "Wool Blend Blazer", modelCode: "mv-100")], Ct);

        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(1, second.GetProperty("updated").GetInt32());
        Assert.Equal(0, second.GetProperty("snapshotsWritten").GetInt32());
        Assert.True(await LastSeenAtAsync(sellerSlug, externalId) > firstSeenAt);
        Assert.Equal(1, await SnapshotCountAsync(sellerSlug, externalId));

        // 3. A new price is a new snapshot.
        var third = await client.UpsertOkAsync(sellerSlug, [Item(externalId, 1099.90m, brand, "Wool Blend Blazer", modelCode: "mv-100")], Ct);

        Assert.Equal(1, third.GetProperty("updated").GetInt32());
        Assert.Equal(1, third.GetProperty("snapshotsWritten").GetInt32());
        Assert.Equal(2, await SnapshotCountAsync(sellerSlug, externalId));
    }

    [Fact]
    public async Task AModelCodeFromAnotherSellerAttachesToTheSameProductAndOffersAreOrderedByEffectivePrice()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Koton");
        var brandStore = $"{Slug(brand)}-store";

        // Trendyol is cheaper by sticker price (100) but charges 49.90 shipping: effective 149.90.
        await client.UpsertOkAsync("trendyol", [Item($"ty-{Guid.NewGuid():N}", 100m, brand, "Oversize Gomlek", modelCode: "KT-77", shippingCost: 49.90m, category: "ust-giyim/gomlek")], Ct);

        // The brand store (same model code, different case) is dearer (120) but ships free: effective 120.
        var second = await client.UpsertOkAsync(brandStore, [Item($"bs-{Guid.NewGuid():N}", 120m, brand, "Oversize Gomlek", modelCode: "kt-77", shippingCost: 0m, category: "ust-giyim/gomlek")], Ct);
        Assert.Equal(1, second.GetProperty("created").GetInt32());

        var list = await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}", Ct);
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        var item = list.GetProperty("items")[0];
        Assert.Equal(2, item.GetProperty("offerCount").GetInt32());
        Assert.Equal(100m, item.GetProperty("lowestPrice").GetDecimal());

        var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{item.GetProperty("id").GetGuid()}", Ct);
        var offers = detail.GetProperty("offers");
        Assert.Equal(2, offers.GetArrayLength());

        Assert.Equal(brandStore, offers[0].GetProperty("seller").GetProperty("slug").GetString());
        Assert.Equal("BrandStore", offers[0].GetProperty("seller").GetProperty("kind").GetString());
        Assert.Equal(120m, offers[0].GetProperty("effectivePrice").GetDecimal());

        Assert.Equal("trendyol", offers[1].GetProperty("seller").GetProperty("slug").GetString());
        Assert.Equal("Marketplace", offers[1].GetProperty("seller").GetProperty("kind").GetString());
        Assert.Equal(149.90m, offers[1].GetProperty("effectivePrice").GetDecimal());
        Assert.Equal(100m, offers[1].GetProperty("currentPrice").GetDecimal());
    }

    [Fact]
    public async Task ReachingTheFreeShippingThresholdDropsTheShippingCost()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Defacto");

        await client.UpsertOkAsync("hepsiburada", [Item($"hb-{Guid.NewGuid():N}", 300m, brand, "Kazak", shippingCost: 39.90m, freeShippingThreshold: 250m)], Ct);

        var productId = await client.FindProductIdAsync(Slug(brand), Ct);
        var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}", Ct);

        Assert.Equal(300m, detail.GetProperty("offers")[0].GetProperty("effectivePrice").GetDecimal());
    }

    [Fact]
    public async Task ABatchWithOneBadItemReturnsOkWithOneErrorAndKeepsTheRest()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Lcw");
        var sellerSlug = $"{Slug(brand)}-store";
        var goodA = $"a-{Guid.NewGuid():N}";
        var bad = $"b-{Guid.NewGuid():N}";
        var goodC = $"c-{Guid.NewGuid():N}";

        var result = await client.UpsertOkAsync(
            sellerSlug,
            [
                Item(goodA, 100m, brand, "Tisort A", category: "ust-giyim/tisort"),
                Item(bad, 100m, brand, "Tisort B", category: "no-such/category"),
                Item(goodC, 100m, brand, "Tisort C", category: "ust-giyim/tisort"),
            ],
            Ct);

        Assert.Equal(2, result.GetProperty("created").GetInt32());
        var error = Assert.Single(result.GetProperty("errors").EnumerateArray());
        Assert.Equal(1, error.GetProperty("index").GetInt32());
        Assert.Equal(bad, error.GetProperty("externalId").GetString());
        Assert.Equal("Catalog.CategoryNotFound", error.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        Assert.Equal([goodA, goodC], await ExternalIdsAsync(sellerSlug));
    }

    [Fact]
    public async Task AnItemThatFailsInTheDatabaseOnlyLosesItself()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Fabrika");

        // A brand whose name collides with the incoming one but whose slug differs, so the lookup by slug misses
        // and inserting the "new" brand violates the unique name index. The whole 3-item group then rolls back.
        var clash = Unique("Clash");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            db.Brands.Add(new Brand { Name = clash, Slug = $"{Slug(clash)}-custom" });
            await db.SaveChangesAsync(Ct);
        }

        var result = await client.UpsertOkAsync(
            "trendyol",
            [
                Item($"ok1-{Guid.NewGuid():N}", 50m, brand, "Bot One", category: "ayakkabi/bot"),
                Item($"bad-{Guid.NewGuid():N}", 50m, clash, "Bot Two", category: "ayakkabi/bot"),
                Item($"ok2-{Guid.NewGuid():N}", 50m, brand, "Bot Three", category: "ayakkabi/bot"),
            ],
            Ct);

        Assert.Equal(2, result.GetProperty("created").GetInt32());
        var error = Assert.Single(result.GetProperty("errors").EnumerateArray());
        Assert.Equal(1, error.GetProperty("index").GetInt32());
        Assert.Equal("Catalog.ItemFailed", error.GetProperty("errorCode").GetString());

        await using var verify = factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<KombinDbContext>();
        Assert.Equal(2, await verifyDb.Products.CountAsync(product => product.Brand.Slug == Slug(brand), Ct));
        Assert.False(await verifyDb.Products.AnyAsync(product => product.Brand.Name == clash, Ct));
    }

    [Fact]
    public async Task ProductsWithTheSameNameGetNumericSlugSuffixes()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Mango");

        await client.UpsertOkAsync("trendyol",
        [
            Item($"m1-{Guid.NewGuid():N}", 100m, brand, "Basic Tişört"),
            Item($"m2-{Guid.NewGuid():N}", 110m, brand, "Basic Tişört"),
            Item($"m3-{Guid.NewGuid():N}", 120m, brand, "Basic Tişört"),
        ], Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        var slugs = await db.Products.Where(product => product.Brand.Slug == Slug(brand)).Select(product => product.Slug).OrderBy(slug => slug).ToListAsync(Ct);

        Assert.Equal(["basic-tisort", "basic-tisort-2", "basic-tisort-3"], slugs);
    }

    [Fact]
    public async Task ACanonicalProductIdRelinksAnExistingOfferAndDeactivatesTheProductItLeft()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Zara");
        var externalId = $"z1-{Guid.NewGuid():N}";

        await client.UpsertOkAsync("trendyol",
        [
            Item(externalId, 500m, brand, "Coat One", category: "dis-giyim/kaban"),
            Item($"z2-{Guid.NewGuid():N}", 520m, brand, "Coat Two", category: "dis-giyim/kaban"),
        ], Ct);

        var products = await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}&sort=priceAsc", Ct);
        var coatOne = products.GetProperty("items")[0].GetProperty("id").GetGuid();
        var coatTwo = products.GetProperty("items")[1].GetProperty("id").GetGuid();

        // Same listing, now claiming to be Coat Two.
        var relinked = await client.UpsertOkAsync("trendyol", [Item(externalId, 500m, brand, "Coat One", category: "dis-giyim/kaban", canonicalProductId: coatTwo)], Ct);
        Assert.Equal(1, relinked.GetProperty("updated").GetInt32());

        var detailTwo = await client.GetJsonAsync($"/api/v1/catalog/products/{coatTwo}", Ct);
        Assert.Equal(2, detailTwo.GetProperty("offers").GetArrayLength());

        var detailOne = await client.GetJsonAsync($"/api/v1/catalog/products/{coatOne}", Ct);
        Assert.False(detailOne.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, detailOne.GetProperty("offers").GetArrayLength());
    }

    [Fact]
    public async Task AnUnknownCanonicalProductIsAPerItemError()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Pull");

        var result = await client.UpsertOkAsync("trendyol", [Item($"p-{Guid.NewGuid():N}", 90m, brand, "Hoodie", canonicalProductId: Guid.NewGuid())], Ct);

        Assert.Equal(0, result.GetProperty("created").GetInt32());
        Assert.Equal("Catalog.ProductNotFound", result.GetProperty("errors")[0].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AnUnknownSellerThatIsNotABrandStoreIsRejected()
    {
        using var client = factory.CreateClient();

        using var response = await client.UpsertAsync("no-such-seller", [Item("x", 10m, "Anything", "Thing")], cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Catalog.SellerNotFound", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ABrandStoreSlugMustMatchTheItemsBrand()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Adidas");

        var result = await client.UpsertOkAsync("someone-elses-store", [Item($"s-{Guid.NewGuid():N}", 10m, brand, "Shoe", category: "ayakkabi/sneaker")], Ct);

        Assert.Equal(0, result.GetProperty("created").GetInt32());
        Assert.Equal("Catalog.SellerNotFound", result.GetProperty("errors")[0].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task InternalEndpointWithoutKeyIsUnauthorized()
    {
        using var client = factory.CreateClient();

        using var response = await client.UpsertAsync("trendyol", [Item("x", 10m, "Anything", "Thing")], internalKey: null, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Catalog.InvalidInternalKey", problem.GetProperty("errorCode").GetString());
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task InternalEndpointWithWrongKeyIsUnauthorized()
    {
        using var client = factory.CreateClient();

        using var response = await client.UpsertAsync("trendyol", [Item("x", 10m, "Anything", "Thing")], internalKey: "not-the-key", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Catalog.InvalidInternalKey", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AUserAccessTokenIsNotAnInternalKey()
    {
        using var client = factory.CreateClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);

        using var response = await client.UpsertAsync("trendyol", [Item("x", 10m, "Anything", "Thing")], internalKey: tokens.AccessToken, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidationFailuresAreKeyedByItemIndex()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Bad");

        using var response = await client.UpsertAsync(
            "trendyol",
            [Item("ok", 10m, brand, "Fine"), Item("neg", -10m, brand, "Negative")],
            cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Validation.Failed", problem.GetProperty("errorCode").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("items[1].price", out _));
    }

    [Fact]
    public async Task ABatchOverTheLimitIsRejected()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Big");
        var items = Enumerable.Range(0, 501).Select(index => Item($"e{index}", 10m, brand, "Thing")).ToArray();

        using var response = await client.UpsertAsync("trendyol", items, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Catalog.BatchTooLarge", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task EmptyBatchIsAValidationError()
    {
        using var client = factory.CreateClient();

        using var response = await client.UpsertAsync("trendyol", [], cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.True(problem.GetProperty("errors").TryGetProperty("items", out _));
    }

    [Fact]
    public async Task MoreThanOneGroupOfItemsIsProcessed()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Many");
        var items = Enumerable.Range(0, 120).Select(index => Item($"many-{Guid.NewGuid():N}", 10m + index, brand, $"Thing {index}")).ToArray();

        var result = await client.UpsertOkAsync("trendyol", items, Ct);

        Assert.Equal(120, result.GetProperty("created").GetInt32());
        Assert.Equal(120, result.GetProperty("snapshotsWritten").GetInt32());
        Assert.Equal(0, result.GetProperty("errors").GetArrayLength());
    }

    private async Task<DateTimeOffset> LastSeenAtAsync(string sellerSlug, string externalId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        return await db.Offers
            .Where(offer => offer.Seller.Slug == sellerSlug && offer.ExternalId == externalId)
            .Select(offer => offer.LastSeenAt)
            .SingleAsync(Ct);
    }

    private async Task<int> SnapshotCountAsync(string sellerSlug, string externalId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        return await db.PriceSnapshots.CountAsync(snapshot => snapshot.Offer.Seller.Slug == sellerSlug && snapshot.Offer.ExternalId == externalId, Ct);
    }

    private async Task<List<string>> ExternalIdsAsync(string sellerSlug)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        return await db.Offers.Where(offer => offer.Seller.Slug == sellerSlug).OrderBy(offer => offer.ExternalId).Select(offer => offer.ExternalId).ToListAsync(Ct);
    }
}
