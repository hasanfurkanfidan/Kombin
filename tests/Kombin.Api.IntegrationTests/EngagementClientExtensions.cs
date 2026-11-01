using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>A registered user and a client that sends their bearer token. Redirects are not followed, so a 302 can be inspected.</summary>
internal sealed record TestUser(HttpClient Client, Guid Id) : IDisposable
{
    public void Dispose() => Client.Dispose();
}

/// <summary>A product with one brand-store offer, as created through the internal upsert.</summary>
internal sealed record SeededProduct(string Brand, string SellerSlug, string ExternalId, Guid ProductId, Guid OfferId)
{
    public object Item(decimal price, string stockStatus = "InStock", string[]? sizes = null, decimal? shippingCost = null) =>
        CatalogClientExtensions.Item(ExternalId, price, Brand, "Blazer", stockStatus: stockStatus, sizes: sizes, shippingCost: shippingCost);
}

internal static class EngagementClientExtensions
{
    public static HttpClient CreateAnonymousClient(this ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static async Task<TestUser> CreateUserAsync(this ApiFactory factory, CancellationToken cancellationToken)
    {
        var client = factory.CreateAnonymousClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: cancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var me = await client.GetJsonAsync("/api/v1/me", cancellationToken);

        return new TestUser(client, me.GetProperty("id").GetGuid());
    }

    /// <summary>Upserts one offer for a fresh brand and returns the IDs the engagement endpoints need.</summary>
    public static async Task<SeededProduct> SeedProductAsync(
        this ApiFactory factory,
        HttpClient client,
        decimal price,
        string stockStatus = "InStock",
        string[]? sizes = null,
        decimal? shippingCost = null,
        CancellationToken cancellationToken = default)
    {
        var brand = Unique("Eng");
        var sellerSlug = $"{Slug(brand)}-store";
        var externalId = $"eng-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(sellerSlug, [Item(externalId, price, brand, "Blazer", stockStatus: stockStatus, sizes: sizes, shippingCost: shippingCost)], cancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        var offer = await db.Offers
            .Where(candidate => candidate.Seller.Slug == sellerSlug && candidate.ExternalId == externalId)
            .Select(candidate => new { candidate.Id, candidate.ProductId })
            .SingleAsync(cancellationToken);

        return new SeededProduct(brand, sellerSlug, externalId, offer.ProductId, offer.Id);
    }

    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient client, string url, object body, CancellationToken cancellationToken) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) }, cancellationToken);

    public static Task<HttpResponseMessage> CreateAlertAsync(
        this HttpClient client, Guid productId, string kind, decimal? targetPrice, string[]? sizes, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync("/api/v1/me/alerts", new { productId, kind, targetPrice, sizes }, cancellationToken);

    /// <summary>Creates an alert, asserts a 201 and returns its ID.</summary>
    public static async Task<Guid> CreateAlertOkAsync(
        this HttpClient client, Guid productId, string kind, decimal? targetPrice = null, string[]? sizes = null, CancellationToken cancellationToken = default)
    {
        using var response = await client.CreateAlertAsync(productId, kind, targetPrice, sizes, cancellationToken);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync(cancellationToken));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return body.GetProperty("id").GetGuid();
    }
}
