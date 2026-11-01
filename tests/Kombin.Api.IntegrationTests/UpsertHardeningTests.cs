using System.Net;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>The additions to the internal upsert: ids in the response, brand details, and the image limit.</summary>
public sealed class UpsertHardeningTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachStoredItemComesBackWithItsProductAndOfferIds()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Ids");
        var sellerSlug = $"{Slug(brand)}-store";
        var externalId = $"ids-{Guid.NewGuid():N}";

        var first = await client.UpsertOkAsync(sellerSlug, [Item(externalId, 100m, brand, "Ids Shirt")], Ct);

        var created = Assert.Single(first.GetProperty("results").EnumerateArray());
        Assert.Equal(0, created.GetProperty("index").GetInt32());
        Assert.Equal(externalId, created.GetProperty("externalId").GetString());
        Assert.True(created.GetProperty("created").GetBoolean());

        var stored = await factory.WithDbAsync(db => db.Offers
            .Where(offer => offer.Seller.Slug == sellerSlug && offer.ExternalId == externalId)
            .Select(offer => new { OfferId = offer.Id, offer.ProductId })
            .SingleAsync(Ct));
        Assert.Equal(stored.OfferId, created.GetProperty("offerId").GetGuid());
        Assert.Equal(stored.ProductId, created.GetProperty("productId").GetGuid());

        // The same listing again: same ids, created false.
        var second = await client.UpsertOkAsync(sellerSlug, [Item(externalId, 100m, brand, "Ids Shirt")], Ct);

        var updated = Assert.Single(second.GetProperty("results").EnumerateArray());
        Assert.False(updated.GetProperty("created").GetBoolean());
        Assert.Equal(stored.OfferId, updated.GetProperty("offerId").GetGuid());
        Assert.Equal(stored.ProductId, updated.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task ResultsAndErrorsTogetherAccountForEveryItemByIndex()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Mixed");
        var sellerSlug = $"{Slug(brand)}-store";
        var goodA = $"a-{Guid.NewGuid():N}";
        var bad = $"b-{Guid.NewGuid():N}";
        var goodC = $"c-{Guid.NewGuid():N}";

        var response = await client.UpsertOkAsync(
            sellerSlug,
            [
                Item(goodA, 100m, brand, "Mixed A", category: "ust-giyim/tisort"),
                Item(bad, 100m, brand, "Mixed B", category: "no-such/category"),
                Item(goodC, 100m, brand, "Mixed C", category: "ust-giyim/tisort"),
            ],
            Ct);

        var results = response.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal([0, 2], results.Select(result => result.GetProperty("index").GetInt32()));
        Assert.Equal([goodA, goodC], results.Select(result => result.GetProperty("externalId").GetString()));
        Assert.All(results, result => Assert.NotEqual(Guid.Empty, result.GetProperty("offerId").GetGuid()));
        Assert.Equal(1, Assert.Single(response.GetProperty("errors").EnumerateArray()).GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task ARelinkedListingReportsTheProductItNowBelongsTo()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Relink");
        var sellerSlug = $"{Slug(brand)}-store";
        var externalId = $"re-{Guid.NewGuid():N}";
        await client.UpsertOkAsync(sellerSlug, [Item(externalId, 100m, brand, "Relink One"), Item($"re2-{Guid.NewGuid():N}", 100m, brand, "Relink Two")], Ct);
        var products = await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}&sort=priceAsc", Ct);
        var target = products.GetProperty("items")[1].GetProperty("id").GetGuid();

        var relinked = await client.UpsertOkAsync(sellerSlug, [Item(externalId, 100m, brand, "Relink One", canonicalProductId: target)], Ct);

        Assert.Equal(target, relinked.GetProperty("results")[0].GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task ABrandCreatedWithWebsiteAndLogoKeepsThem()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Brand");

        await client.UpsertOkAsync(
            "trendyol",
            [Item($"bd-{Guid.NewGuid():N}", 100m, brand, "Brand Shirt", brandWebsiteUrl: "https://www.brand.example.com", brandLogoUrl: "https://cdn.brand.example.com/logo.svg")],
            Ct);

        var stored = await factory.WithDbAsync(db => db.Brands.AsNoTracking().SingleAsync(candidate => candidate.Slug == Slug(brand), Ct));
        Assert.Equal("https://www.brand.example.com", stored.WebsiteUrl);
        Assert.Equal("https://cdn.brand.example.com/logo.svg", stored.LogoUrl);
    }

    [Fact]
    public async Task ABrandThatHasNoWebsiteOrLogoGetsThemLaterAndThenKeepsThem()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Late");
        var existing = $"late-{Guid.NewGuid():N}";
        await client.UpsertOkAsync("trendyol", [Item(existing, 100m, brand, "Late One")], Ct);

        // A new listing of the brand brings the details; so does a plain re-observation of an existing one.
        await client.UpsertOkAsync("trendyol", [Item(existing, 100m, brand, "Late One", brandWebsiteUrl: "https://www.late.example.com")], Ct);
        await client.UpsertOkAsync("trendyol", [Item($"late2-{Guid.NewGuid():N}", 100m, brand, "Late Two", brandLogoUrl: "https://cdn.late.example.com/logo.png")], Ct);

        var filled = await factory.WithDbAsync(db => db.Brands.AsNoTracking().SingleAsync(candidate => candidate.Slug == Slug(brand), Ct));
        Assert.Equal("https://www.late.example.com", filled.WebsiteUrl);
        Assert.Equal("https://cdn.late.example.com/logo.png", filled.LogoUrl);

        // Later values never overwrite what the brand has.
        await client.UpsertOkAsync(
            "trendyol",
            [Item($"late3-{Guid.NewGuid():N}", 100m, brand, "Late Three", brandWebsiteUrl: "https://other.example.com", brandLogoUrl: "https://other.example.com/logo.png")],
            Ct);

        var kept = await factory.WithDbAsync(db => db.Brands.AsNoTracking().SingleAsync(candidate => candidate.Slug == Slug(brand), Ct));
        Assert.Equal("https://www.late.example.com", kept.WebsiteUrl);
        Assert.Equal("https://cdn.late.example.com/logo.png", kept.LogoUrl);
    }

    [Fact]
    public async Task MoreThanSixImagesAreRejected()
    {
        using var client = factory.CreateClient();
        var images = Enumerable.Range(1, 7).Select(number => $"https://img.example.com/{number}.jpg").ToArray();

        using var response = await client.UpsertAsync("trendyol", [Item("img-x", 10m, Unique("Img"), "Many Images", imageUrls: images)], cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("items[0].imageUrls", out _));
    }

    [Fact]
    public async Task SixImagesAreStored()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Six");
        var images = Enumerable.Range(1, 6).Select(number => $"https://img.example.com/{brand}/{number}.jpg").ToArray();

        await client.UpsertOkAsync("trendyol", [Item($"six-{Guid.NewGuid():N}", 10m, brand, "Six Images", imageUrls: images)], Ct);

        Assert.Equal(6, await factory.WithDbAsync(db => db.Products.Where(product => product.Brand.Slug == Slug(brand)).SelectMany(product => product.Images).CountAsync(Ct)));
    }

    [Fact]
    public async Task ObviouslyInvalidImageAndBrandUrlsAreRejectedWithKeyedErrors()
    {
        using var client = factory.CreateClient();

        using var response = await client.UpsertAsync(
            "trendyol",
            [Item("url-x", 10m, Unique("Url"), "Bad Urls", imageUrls: ["https://ok.example.com/a.jpg", "javascript:alert(1)"], brandWebsiteUrl: "mavi.com", brandLogoUrl: "ftp://x.example.com/l.png")],
            cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ReadProblemAsync(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("items[0].imageUrls[1]", out _));
        Assert.True(errors.TryGetProperty("items[0].product.brandWebsiteUrl", out _));
        Assert.True(errors.TryGetProperty("items[0].product.brandLogoUrl", out _));
    }
}
