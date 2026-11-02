using System.Net.Http.Json;
using System.Text.Json;

namespace Kombin.Api.IntegrationTests;

/// <summary>The OpenAPI document is generated from the endpoints and their records; these checks keep the new surface from silently missing.</summary>
public sealed class OpenApiTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonElement> DocumentAsync()
    {
        using var client = factory.CreateClient();

        return await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);
    }

    [Fact]
    public async Task TheProductListItemDocumentsTheCheapestSeller()
    {
        var document = await DocumentAsync();
        var paged = document.GetProperty("paths").GetProperty("/api/v1/catalog/products").GetProperty("get").GetProperty("responses").GetProperty("200").GetRawText();
        Assert.Contains("ProductListItem", paged, StringComparison.Ordinal);

        var item = document.GetProperty("components").GetProperty("schemas").GetProperty("ProductListItem").GetProperty("properties");
        Assert.True(item.TryGetProperty("cheapestSeller", out var cheapestSeller));
        Assert.Contains("SellerRef", cheapestSeller.GetRawText(), StringComparison.Ordinal);

        var seller = document.GetProperty("components").GetProperty("schemas").GetProperty("SellerRef").GetProperty("properties");
        Assert.True(seller.TryGetProperty("name", out _));
        Assert.True(seller.TryGetProperty("slug", out _));
    }

    [Fact]
    public async Task TheAnalyticsEndpointsAreDocumented()
    {
        var paths = (await DocumentAsync()).GetProperty("paths");

        Assert.True(paths.GetProperty("/api/v1/events").GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(paths.GetProperty("/api/v1/internal/analytics/weekly").TryGetProperty("get", out _));
    }
}
