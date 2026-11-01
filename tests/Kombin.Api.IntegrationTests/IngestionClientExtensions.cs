using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Catalog.Jobs;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>The internal catalog endpoints a worker uses besides the upsert: seller bootstrap, seller listing and the sweep.</summary>
internal static class IngestionClientExtensions
{
    public static Task<HttpResponseMessage> SendInternalAsync(
        this HttpClient client,
        HttpMethod method,
        string url,
        object? body = null,
        string? internalKey = ApiFactory.InternalApiKey,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (internalKey is not null)
        {
            request.Headers.Add("X-Internal-Key", internalKey);
        }

        return client.SendAsync(request, cancellationToken);
    }

    /// <summary>A seller body as a worker would send it; the name is derived from the slug unless given.</summary>
    public static object SellerBody(
        string slug,
        string kind = "Marketplace",
        string? name = null,
        string websiteUrl = "https://www.seller.example.com",
        string? affiliateNetwork = null,
        string? affiliateUrlTemplate = null,
        Dictionary<string, string>? affiliateParams = null,
        bool? isActive = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name ?? $"Seller {slug}",
            ["kind"] = kind,
            ["websiteUrl"] = websiteUrl,
            ["affiliateNetwork"] = affiliateNetwork,
            ["affiliateUrlTemplate"] = affiliateUrlTemplate,
            ["affiliateParams"] = affiliateParams,
        };

        // Left out rather than null when not given: that is what the default of true is for.
        if (isActive is { } active)
        {
            body["isActive"] = active;
        }

        return body;
    }

    public static Task<HttpResponseMessage> PutSellerAsync(
        this HttpClient client, string slug, object body, string? internalKey = ApiFactory.InternalApiKey, CancellationToken cancellationToken = default) =>
        client.SendInternalAsync(HttpMethod.Put, $"/api/v1/internal/catalog/sellers/{slug}", body, internalKey, cancellationToken);

    /// <summary>Puts a seller and asserts a 200, returning the body.</summary>
    public static async Task<JsonElement> PutSellerOkAsync(this HttpClient client, string slug, object body, CancellationToken cancellationToken = default)
    {
        using var response = await client.PutSellerAsync(slug, body, cancellationToken: cancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>Registers a fresh marketplace-style seller (not a "-store" slug, so the auto-create path could not have made it).</summary>
    public static async Task<(string Slug, Guid Id)> CreateSellerAsync(this HttpClient client, string prefix = "Seller", CancellationToken cancellationToken = default)
    {
        var slug = $"{Slug(Unique(prefix))}-outlet";
        var seller = await client.PutSellerOkAsync(slug, SellerBody(slug), cancellationToken);

        return (slug, seller.GetProperty("id").GetGuid());
    }

    public static Task<HttpResponseMessage> GetSellerOffersAsync(
        this HttpClient client, string slug, string query = "", string? internalKey = ApiFactory.InternalApiKey, CancellationToken cancellationToken = default) =>
        client.SendInternalAsync(HttpMethod.Get, $"/api/v1/internal/catalog/sellers/{slug}/offers{query}", internalKey: internalKey, cancellationToken: cancellationToken);

    /// <summary>Lists a seller's offers and asserts a 200, returning the page.</summary>
    public static async Task<JsonElement> GetSellerOffersOkAsync(this HttpClient client, string slug, string query = "", CancellationToken cancellationToken = default)
    {
        using var response = await client.GetSellerOffersAsync(slug, query, cancellationToken: cancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static Task<HttpResponseMessage> SweepAsync(
        this HttpClient client, object? body = null, string? internalKey = ApiFactory.InternalApiKey, CancellationToken cancellationToken = default) =>
        client.SendInternalAsync(HttpMethod.Post, "/api/v1/internal/catalog/offers/sweep", body, internalKey, cancellationToken);

    /// <summary>Sweeps one seller through the endpoint and asserts a 200, returning the counts.</summary>
    public static async Task<JsonElement> SweepSellerOkAsync(this HttpClient client, string sellerSlug, CancellationToken cancellationToken = default)
    {
        using var response = await client.SweepAsync(new { sellerSlug }, cancellationToken: cancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>Runs the sweep job directly for one seller, the way the tests run every other job.</summary>
    public static Task<SweepSummary> SweepSellerAsync(this ApiFactory factory, Guid sellerId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<OfferSweepJob, SweepSummary>(job => job.SweepAsync(sellerId, cancellationToken));

    /// <summary>Pretends the worker last saw the listing <paramref name="age"/> ago.</summary>
    public static Task AgeOfferAsync(this ApiFactory factory, string sellerSlug, string externalId, TimeSpan age, CancellationToken cancellationToken) =>
        factory.WithDbAsync(async db =>
        {
            var offerId = await db.Offers
                .Where(offer => offer.Seller.Slug == sellerSlug && offer.ExternalId == externalId)
                .Select(offer => offer.Id)
                .SingleAsync(cancellationToken);
            var lastSeenAt = DateTimeOffset.UtcNow - age;

            return await db.Offers.Where(offer => offer.Id == offerId).ExecuteUpdateAsync(setters => setters.SetProperty(offer => offer.LastSeenAt, lastSeenAt), cancellationToken);
        });
}
