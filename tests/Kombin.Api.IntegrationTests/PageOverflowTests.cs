using System.Net;
using System.Text.Json;

namespace Kombin.Api.IntegrationTests;

/// <summary>Night review item 4 (the feed validates its page, 1 to 20, already): a page number near <see cref="int.MaxValue"/> must not overflow the offset (PostgreSQL refuses a negative one).</summary>
public sealed class PageOverflowTests(ApiFactory factory)
{
    private const string Huge = "page=2147483647&pageSize=100";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/v1/catalog/products")]
    [InlineData("/api/v1/catalog/brands")]
    [InlineData("/api/v1/me/clicks")]
    [InlineData("/api/v1/me/saved")]
    [InlineData("/api/v1/me/alerts/triggers")]
    [InlineData("/api/v1/me/notifications")]
    [InlineData("/api/v1/me/wardrobe/items")]
    public async Task AHugePageIsAnEmptyPageNotAnError(string path)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.GetAsync($"{path}?{Huge}", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task TheCatalogListIsPublicSoAnAnonymousCallerCanTryItToo()
    {
        using var anonymous = factory.CreateAnonymousClient();

        using var response = await anonymous.GetAsync($"/api/v1/catalog/products?{Huge}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheWorkersSellerListingClampsItToo()
    {
        using var anonymous = factory.CreateAnonymousClient();
        var product = await factory.SeedProductAsync(anonymous, 300m, cancellationToken: Ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/internal/catalog/sellers/{product.SellerSlug}/offers?page=2147483647&pageSize=1000");
        request.Headers.Add("X-Internal-Key", ApiFactory.InternalApiKey);

        using var response = await anonymous.SendAsync(request, Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }
}
