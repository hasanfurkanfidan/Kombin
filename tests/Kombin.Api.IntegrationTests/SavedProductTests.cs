using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class SavedProductTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SavingIsIdempotentAndTheIdsEndpointFollowsIt()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var saved = await user.Client.PutAsync($"/api/v1/me/saved/{product.ProductId}", content: null, Ct);
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        }

        var ids = await user.Client.GetJsonAsync("/api/v1/me/saved/ids", Ct);
        Assert.Equal([product.ProductId], ids.GetProperty("productIds").EnumerateArray().Select(id => id.GetGuid()));

        var list = await user.Client.GetJsonAsync("/api/v1/me/saved", Ct);
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var removed = await user.Client.DeleteAsync($"/api/v1/me/saved/{product.ProductId}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        ids = await user.Client.GetJsonAsync("/api/v1/me/saved/ids", Ct);
        Assert.Equal(0, ids.GetProperty("productIds").GetArrayLength());
    }

    [Fact]
    public async Task SavingAnUnknownProductIsNotFound()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutAsync($"/api/v1/me/saved/{Guid.NewGuid()}", content: null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Catalog.ProductNotFound", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task EveryEndpointNeedsASignedInUser()
    {
        using var client = factory.CreateAnonymousClient();
        var id = Guid.NewGuid();

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Put, $"/api/v1/me/saved/{id}"),
            (HttpMethod.Delete, $"/api/v1/me/saved/{id}"),
            (HttpMethod.Get, "/api/v1/me/saved"),
            (HttpMethod.Get, "/api/v1/me/saved/ids"),
            (HttpMethod.Post, "/api/v1/me/alerts"),
            (HttpMethod.Get, "/api/v1/me/alerts"),
            (HttpMethod.Patch, $"/api/v1/me/alerts/{id}"),
            (HttpMethod.Delete, $"/api/v1/me/alerts/{id}"),
            (HttpMethod.Get, "/api/v1/me/alerts/triggers"),
            (HttpMethod.Get, "/api/v1/me/clicks"),
        })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, url), Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task TheSavedListIsNewestFirstAndEachUserOnlySeesTheirOwn()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var first = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        var second = await factory.SeedProductAsync(user.Client, 400m, cancellationToken: Ct);
        var third = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);

        foreach (var product in new[] { first, second, third })
        {
            using var saved = await user.Client.PutAsync($"/api/v1/me/saved/{product.ProductId}", content: null, Ct);
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        }

        using var othersSave = await other.Client.PutAsync($"/api/v1/me/saved/{first.ProductId}", content: null, Ct);

        var page = await user.Client.GetJsonAsync("/api/v1/me/saved?page=1&pageSize=2", Ct);
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(
            [third.ProductId, second.ProductId],
            page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));

        var othersList = await other.Client.GetJsonAsync("/api/v1/me/saved", Ct);
        Assert.Equal(1, othersList.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task SavedItemsHaveTheCatalogListShapeWithSavedAtAndIsActive()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 450m, cancellationToken: Ct);
        using var saved = await user.Client.PutAsync($"/api/v1/me/saved/{product.ProductId}", content: null, Ct);

        var catalogItem = (await user.Client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(product.Brand)}", Ct)).GetProperty("items")[0];
        var savedItem = (await user.Client.GetJsonAsync("/api/v1/me/saved", Ct)).GetProperty("items")[0];

        // Everything the catalog list item has is there, with the same values.
        foreach (var property in catalogItem.EnumerateObject())
        {
            Assert.True(savedItem.TryGetProperty(property.Name, out var value), $"'{property.Name}' is missing from the saved item.");
            Assert.Equal(property.Value.GetRawText(), value.GetRawText());
        }

        Assert.True(savedItem.GetProperty("isActive").GetBoolean());
        Assert.True(savedItem.TryGetProperty("savedAt", out _));
    }

    [Fact]
    public async Task ADelistedProductStaysInTheSavedListAsInactive()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 450m, cancellationToken: Ct);
        using var saved = await user.Client.PutAsync($"/api/v1/me/saved/{product.ProductId}", content: null, Ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            await db.Products.Where(candidate => candidate.Id == product.ProductId).ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.IsActive, false), Ct);
            await db.Offers.Where(candidate => candidate.ProductId == product.ProductId).ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.IsActive, false), Ct);
        }

        var item = (await user.Client.GetJsonAsync("/api/v1/me/saved", Ct)).GetProperty("items")[0];

        Assert.Equal(product.ProductId, item.GetProperty("id").GetGuid());
        Assert.False(item.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, item.GetProperty("offerCount").GetInt32());
    }
}
