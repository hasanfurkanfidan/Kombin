using System.Net;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;
using static Kombin.Api.IntegrationTests.IngestionClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>Seller bootstrap and the seller listing that workers use for delta runs.</summary>
public sealed class SellerIngestionTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewSlug(string prefix) => $"{Slug(Unique(prefix))}-outlet";

    private Task<Seller> SellerAsync(string slug) =>
        factory.WithDbAsync(db => db.Sellers.AsNoTracking().SingleAsync(seller => seller.Slug == slug, Ct));

    // ---- PUT /sellers/{slug} ----

    [Fact]
    public async Task APutCreatesTheSellerThenUpdatesIt()
    {
        using var client = factory.CreateClient();
        var slug = NewSlug("Put");

        var created = await client.PutSellerOkAsync(
            slug,
            SellerBody(
                slug,
                "BrandStore",
                name: $"Put {slug}",
                affiliateNetwork: "awin",
                affiliateUrlTemplate: "https://track.example.com/c?u={url}&aid={p.aid}",
                affiliateParams: new Dictionary<string, string> { ["aid"] = "42" },
                isActive: false),
            Ct);

        Assert.True(created.GetProperty("created").GetBoolean());
        Assert.Equal(slug, created.GetProperty("slug").GetString());
        Assert.Equal("BrandStore", created.GetProperty("kind").GetString());
        Assert.False(created.GetProperty("isActive").GetBoolean());

        var stored = await SellerAsync(slug);
        Assert.Equal(created.GetProperty("id").GetGuid(), stored.Id);
        Assert.Equal(SellerKind.BrandStore, stored.Kind);
        Assert.Equal("awin", stored.AffiliateNetwork);
        Assert.Equal("https://track.example.com/c?u={url}&aid={p.aid}", stored.AffiliateUrlTemplate);
        Assert.Equal("42", stored.AffiliateParams!["aid"]);
        Assert.False(stored.IsActive);

        // Second put: same seller, whole resource replaced; what is left out is cleared.
        var updated = await client.PutSellerOkAsync(slug, SellerBody(slug, "Marketplace", name: $"Renamed {slug}", websiteUrl: "https://new.example.com"), Ct);

        Assert.False(updated.GetProperty("created").GetBoolean());
        Assert.Equal(created.GetProperty("id").GetGuid(), updated.GetProperty("id").GetGuid());

        var restored = await SellerAsync(slug);
        Assert.Equal($"Renamed {slug}", restored.Name);
        Assert.Equal(SellerKind.Marketplace, restored.Kind);
        Assert.Equal("https://new.example.com", restored.WebsiteUrl);
        Assert.Null(restored.AffiliateNetwork);
        Assert.Null(restored.AffiliateUrlTemplate);
        Assert.Null(restored.AffiliateParams);
        Assert.True(restored.IsActive);
    }

    [Fact]
    public async Task ARegisteredSellerCanReceiveOffersWithoutBeingABrandStore()
    {
        using var client = factory.CreateClient();
        var slug = NewSlug("Reg");
        var brand = Unique("Reg");
        await client.PutSellerOkAsync(slug, SellerBody(slug, websiteUrl: "https://www.registered.example.com"), Ct);

        var result = await client.UpsertOkAsync(slug, [Item($"reg-{Guid.NewGuid():N}", 120m, brand, "Registered Shirt")], Ct);

        Assert.Equal(1, result.GetProperty("created").GetInt32());
        var seller = await SellerAsync(slug);
        Assert.Equal(SellerKind.Marketplace, seller.Kind);
        Assert.Equal("https://www.registered.example.com", seller.WebsiteUrl);
    }

    [Fact]
    public async Task APutCanTakeOverASellerTheBrandStoreAutoCreateMade()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Auto");
        var slug = $"{Slug(brand)}-store";
        await client.UpsertOkAsync(slug, [Item($"auto-{Guid.NewGuid():N}", 100m, brand, "Auto Shirt")], Ct);

        var put = await client.PutSellerOkAsync(slug, SellerBody(slug, "BrandStore", name: $"{brand} Official", websiteUrl: "https://www.official.example.com", affiliateNetwork: "own"), Ct);

        Assert.False(put.GetProperty("created").GetBoolean());
        var seller = await SellerAsync(slug);
        Assert.Equal($"{brand} Official", seller.Name);
        Assert.Equal("own", seller.AffiliateNetwork);
    }

    [Fact]
    public async Task ASellerWithoutIsActiveIsActive()
    {
        using var client = factory.CreateClient();
        var slug = NewSlug("Default");

        var seller = await client.PutSellerOkAsync(slug, new { name = $"Default {slug}", kind = "Marketplace", websiteUrl = "https://www.example.com" }, Ct);

        Assert.True(seller.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ANameAnotherSellerUsesIsAConflict()
    {
        using var client = factory.CreateClient();
        var first = NewSlug("Name");
        var second = NewSlug("Name");
        var name = $"Shared {first}";
        await client.PutSellerOkAsync(first, SellerBody(first, name: name), Ct);

        using var response = await client.PutSellerAsync(second, SellerBody(second, name: name), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Catalog.SellerNameTaken", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.False(await factory.WithDbAsync(db => db.Sellers.AnyAsync(seller => seller.Slug == second, Ct)));
    }

    [Fact]
    public async Task APutThatKeepsItsOwnNameIsNotAConflict()
    {
        using var client = factory.CreateClient();
        var slug = NewSlug("Own");
        await client.PutSellerOkAsync(slug, SellerBody(slug), Ct);

        using var response = await client.PutSellerAsync(slug, SellerBody(slug), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InvalidSellersAreRejectedWithKeyedErrors()
    {
        using var client = factory.CreateClient();
        var slug = NewSlug("Bad");

        using var response = await client.PutSellerAsync(
            slug,
            SellerBody(slug, "Outlet", name: "", websiteUrl: "not-a-url", affiliateUrlTemplate: "https://track.example.com/no-placeholder"),
            cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ReadProblemAsync(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("kind", out _));
        Assert.True(errors.TryGetProperty("websiteUrl", out _));
        Assert.True(errors.TryGetProperty("affiliateUrlTemplate", out _));
        Assert.False(await factory.WithDbAsync(db => db.Sellers.AnyAsync(seller => seller.Slug == slug, Ct)));
    }

    [Fact]
    public async Task ASlugThatIsNotKebabCaseIsRejected()
    {
        using var client = factory.CreateClient();

        using var response = await client.PutSellerAsync("Not%20Kebab", SellerBody("whatever"), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("sellerSlug", out _));
    }

    [Fact]
    public async Task PuttingASellerNeedsTheInternalKeyAndABody()
    {
        using var client = factory.CreateClient();
        var slug = NewSlug("Auth");

        using var withoutKey = await client.PutSellerAsync(slug, SellerBody(slug), internalKey: null, cancellationToken: Ct);
        using var withoutBody = await client.SendInternalAsync(HttpMethod.Put, $"/api/v1/internal/catalog/sellers/{slug}", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutBody.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.Sellers.AnyAsync(seller => seller.Slug == slug, Ct)));
    }

    // ---- GET /sellers/{slug}/offers ----

    private static async Task<(string Slug, string[] ExternalIds)> SeedListingAsync(HttpClient client, int count, string prefix = "List")
    {
        var (slug, _) = await client.CreateSellerAsync(prefix, Ct);
        var brand = Unique(prefix);
        var externalIds = Enumerable.Range(1, count).Select(index => $"ext-{index:D3}").ToArray();

        await client.UpsertOkAsync(
            slug,
            [.. externalIds.Select((externalId, index) => Item(externalId, 100m + index, brand, $"Listed Item {index}", modelCode: $"ls-{index:D3}", stockStatus: index % 2 == 0 ? "InStock" : "LowStock"))],
            Ct);

        return (slug, externalIds);
    }

    [Fact]
    public async Task TheListingPagesThroughASellersOffersInExternalIdOrder()
    {
        using var client = factory.CreateClient();
        var (slug, externalIds) = await SeedListingAsync(client, 5);

        var first = await client.GetSellerOffersOkAsync(slug, "?page=1&pageSize=2", Ct);
        var last = await client.GetSellerOffersOkAsync(slug, "?page=3&pageSize=2", Ct);

        Assert.Equal(5, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(externalIds[..2], first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("externalId").GetString()));
        Assert.Equal(externalIds[4..], last.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("externalId").GetString()));
    }

    [Fact]
    public async Task EachListedOfferCarriesWhatAWorkerNeedsToReconcile()
    {
        using var client = factory.CreateClient();
        var (slug, externalIds) = await SeedListingAsync(client, 2);

        var page = await client.GetSellerOffersOkAsync(slug, cancellationToken: Ct);
        var item = page.GetProperty("items")[1];

        Assert.Equal(externalIds[1], item.GetProperty("externalId").GetString());
        Assert.Equal("LS-001", item.GetProperty("modelCode").GetString());
        Assert.Equal(101m, item.GetProperty("currentPrice").GetDecimal());
        Assert.Equal("LowStock", item.GetProperty("stockStatus").GetString());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        Assert.True(item.GetProperty("lastSeenAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));

        var offerAndProduct = await factory.WithDbAsync(db => db.Offers
            .Where(offer => offer.Seller.Slug == slug && offer.ExternalId == externalIds[1])
            .Select(offer => offer.ProductId)
            .SingleAsync(Ct));
        Assert.Equal(offerAndProduct, item.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task TheListingOnlyContainsTheNamedSellersOffersAndKeepsInactiveOnes()
    {
        using var client = factory.CreateClient();
        var (slug, externalIds) = await SeedListingAsync(client, 2);
        await SeedListingAsync(client, 3, "Elsewhere");
        await factory.AgeOfferAsync(slug, externalIds[0], TimeSpan.FromDays(3), Ct);
        var (_, sellerId) = (slug, (await factory.WithDbAsync(db => db.Sellers.SingleAsync(seller => seller.Slug == slug, Ct))).Id);
        await factory.SweepSellerAsync(sellerId, Ct);

        var page = await client.GetSellerOffersOkAsync(slug, cancellationToken: Ct);

        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        var vanished = page.GetProperty("items")[0];
        Assert.False(vanished.GetProperty("isActive").GetBoolean());
        Assert.Equal("OutOfStock", vanished.GetProperty("stockStatus").GetString());
    }

    [Fact]
    public async Task UpdatedAfterKeepsOnlyTheListingsChangedSince()
    {
        using var client = factory.CreateClient();
        var (slug, externalIds) = await SeedListingAsync(client, 3);
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);

        // Bulk update on purpose: it leaves UpdatedAt alone, which the audit interceptor would otherwise move.
        await factory.WithDbAsync(db => db.Offers
            .Where(offer => offer.Seller.Slug == slug && offer.ExternalId != externalIds[2])
            .ExecuteUpdateAsync(setters => setters.SetProperty(offer => offer.UpdatedAt, longAgo), Ct));

        var since = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        var recent = await client.GetSellerOffersOkAsync(slug, $"?updatedAfter={Uri.EscapeDataString(since.ToString("O"))}", Ct);
        var future = await client.GetSellerOffersOkAsync(slug, $"?updatedAfter={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}", Ct);

        Assert.Equal(1, recent.GetProperty("totalCount").GetInt32());
        Assert.Equal(externalIds[2], recent.GetProperty("items")[0].GetProperty("externalId").GetString());
        Assert.Equal(0, future.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ASweepAndAnUpsertBothShowUpInADeltaListing()
    {
        using var client = factory.CreateClient();
        var (slug, externalIds) = await SeedListingAsync(client, 2);
        var sellerId = (await factory.WithDbAsync(db => db.Sellers.SingleAsync(seller => seller.Slug == slug, Ct))).Id;
        var yesterday = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        await factory.WithDbAsync(db => db.Offers.Where(offer => offer.Seller.Slug == slug).ExecuteUpdateAsync(setters => setters.SetProperty(offer => offer.UpdatedAt, yesterday), Ct));

        await factory.AgeOfferAsync(slug, externalIds[0], TimeSpan.FromDays(3), Ct);
        await factory.SweepSellerAsync(sellerId, Ct);

        var delta = await client.GetSellerOffersOkAsync(slug, $"?updatedAfter={Uri.EscapeDataString(yesterday.AddHours(1).ToString("O"))}", Ct);

        // The swept listing moved UpdatedAt, so a worker running a delta sees that it vanished; the untouched one does not appear.
        var item = Assert.Single(delta.GetProperty("items").EnumerateArray());
        Assert.Equal(externalIds[0], item.GetProperty("externalId").GetString());
        Assert.False(item.GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [InlineData(5000, 1000)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public async Task ThePageSizeIsClampedToWhatTheEndpointAllows(int requested, int expected)
    {
        using var client = factory.CreateClient();
        var (slug, _) = await client.CreateSellerAsync("Clamp", Ct);

        var page = await client.GetSellerOffersOkAsync(slug, $"?pageSize={requested}", Ct);

        Assert.Equal(expected, page.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task AnUnknownSellerIsNotFoundAndTheKeyIsRequired()
    {
        using var client = factory.CreateClient();
        var (slug, _) = await client.CreateSellerAsync("Keyed", Ct);

        using var unknown = await client.GetSellerOffersAsync("no-such-seller", cancellationToken: Ct);
        using var withoutKey = await client.GetSellerOffersAsync(slug, internalKey: null, cancellationToken: Ct);
        using var badDate = await client.GetSellerOffersAsync(slug, "?updatedAfter=yesterday", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Catalog.SellerNotFound", (await unknown.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);
    }
}
