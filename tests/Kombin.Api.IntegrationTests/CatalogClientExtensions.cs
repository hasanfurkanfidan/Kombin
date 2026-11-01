using System.Net.Http.Json;
using System.Text.Json;

namespace Kombin.Api.IntegrationTests;

internal static class CatalogClientExtensions
{
    /// <summary>A short unique token so tests sharing one database never see each other's brands, sellers or products.</summary>
    public static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 8)];

    public static string Slug(string value) => value.ToLowerInvariant();

    /// <summary>One item of an upsert body, shaped as an external worker would send it.</summary>
    public static object Item(
        string externalId,
        decimal price,
        string brand,
        string name,
        string? modelCode = null,
        string category = "ust-giyim/blazer",
        decimal? originalPrice = null,
        decimal? shippingCost = null,
        decimal? freeShippingThreshold = null,
        string stockStatus = "InStock",
        string[]? imageUrls = null,
        string color = "Green",
        string audience = "Women",
        Guid? canonicalProductId = null) =>
        new
        {
            externalId,
            url = $"https://shop.example.com/p/{externalId}",
            title = $"{brand} {name}",
            price,
            originalPrice,
            shippingCost,
            freeShippingThreshold,
            stockStatus,
            availableSizes = new[] { "S", "M" },
            imageUrls = imageUrls ?? [$"https://img.example.com/{externalId}.jpg"],
            product = new
            {
                brandName = brand,
                name,
                modelCode,
                categorySlug = category,
                audience,
                primaryColor = color,
                canonicalProductId,
            },
        };

    public static Task<HttpResponseMessage> UpsertAsync(
        this HttpClient client, string sellerSlug, object[] items, string? internalKey = ApiFactory.InternalApiKey, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/internal/catalog/offers/upsert")
        {
            Content = JsonContent.Create(new { sellerSlug, items }),
        };

        if (internalKey is not null)
        {
            request.Headers.Add("X-Internal-Key", internalKey);
        }

        return client.SendAsync(request, cancellationToken);
    }

    /// <summary>Upserts and asserts a 200, returning the summary body.</summary>
    public static async Task<JsonElement> UpsertOkAsync(
        this HttpClient client, string sellerSlug, object[] items, CancellationToken cancellationToken = default)
    {
        using var response = await client.UpsertAsync(sellerSlug, items, cancellationToken: cancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static async Task<Guid> FindProductIdAsync(this HttpClient client, string brandSlug, CancellationToken cancellationToken = default)
    {
        var list = await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={brandSlug}", cancellationToken);
        return list.GetProperty("items")[0].GetProperty("id").GetGuid();
    }
}
