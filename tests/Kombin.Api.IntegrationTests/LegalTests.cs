using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Kombin.Api.IntegrationTests;

/// <summary>The public legal pages and <c>GET /api/v1/legal</c>.</summary>
public sealed class LegalTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        return await response.Content.ReadAsStringAsync(Ct);
    }

    [Theory]
    [InlineData("/legal/privacy")]
    [InlineData("/legal/terms")]
    [InlineData("/legal/delete-account")]
    public async Task EachPageIsPublicTurkishAndMobileFriendlyAndShowsTheVersionAndTheController(string path)
    {
        using var anonymous = factory.CreateClient();

        var html = await PageAsync(anonymous, path);

        Assert.Contains("<html lang=\"tr\">", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">", html, StringComparison.Ordinal);
        Assert.Contains("Sürüm 1.0", html, StringComparison.Ordinal);
        Assert.Contains("Kombin Test A.Ş.", html, StringComparison.Ordinal);
        Assert.Contains("privacy@kombin.test", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePrivacyPageCoversWhatTheBriefAsksFor()
    {
        using var anonymous = factory.CreateClient();

        var html = await PageAsync(anonymous, "/legal/privacy");

        Assert.Contains("Test Mah. 1. Sk. No:1 İstanbul", html, StringComparison.Ordinal);
        Assert.Contains("Veri sorumlusu", html, StringComparison.Ordinal);
        Assert.Contains("Kombin satış yapmaz; bağlantılardan komisyon kazanabilir", html, StringComparison.Ordinal);
        Assert.Contains("Expo", html, StringComparison.Ordinal);
        Assert.Contains("Google ve Apple", html, StringComparison.Ordinal);
        Assert.Contains("Avrupa Birliği", html, StringComparison.Ordinal);
        Assert.Contains("KVKK", html, StringComparison.Ordinal);
        Assert.Contains("7 gün", html, StringComparison.Ordinal);
        Assert.Contains("13 yaşından", html, StringComparison.Ordinal);

        // Every sentence that needs a lawyer carries the marker; the list is in docs/legal-review.md.
        Assert.True(Markers(html) >= 10, "The privacy policy should flag its open legal questions.");
    }

    [Fact]
    public async Task TheTermsAndTheDeletionPageSayTheirPiece()
    {
        using var anonymous = factory.CreateClient();

        var terms = await PageAsync(anonymous, "/legal/terms");
        Assert.Contains("Kombin satış yapmaz; bağlantılardan komisyon kazanabilir", terms, StringComparison.Ordinal);
        Assert.Contains("paylaşım onayı", terms, StringComparison.Ordinal);
        Assert.Contains("24 saat", terms, StringComparison.Ordinal);
        Assert.True(Markers(terms) >= 5);

        var deletion = await PageAsync(anonymous, "/legal/delete-account");
        Assert.Contains("SİL", deletion, StringComparison.Ordinal);
        Assert.Contains("7 gün", deletion, StringComparison.Ordinal);
        Assert.Contains("Anonimleştirilir", deletion, StringComparison.Ordinal);
        Assert.Contains("privacy@kombin.test", deletion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileTheControllerIsNotConfiguredThePagesShowThePlaceholders()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Legal:ControllerName"] = "", ["Legal:ContactEmail"] = "", ["Legal:Address"] = "" })));
        using var client = host.CreateClient();

        var html = await PageAsync(client, "/legal/privacy");

        Assert.Contains("{{CONTROLLER_NAME}}", html, StringComparison.Ordinal);
        Assert.Contains("{{CONTACT_EMAIL}}", html, StringComparison.Ordinal);
        Assert.Contains("{{ADDRESS}}", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVersionEndpointGivesTheVersionAndAbsoluteUrls()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync("/api/v1/legal", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("1.0", body.GetProperty("version").GetString());
        Assert.Equal("http://localhost/legal/privacy", body.GetProperty("privacyUrl").GetString());
        Assert.Equal("http://localhost/legal/terms", body.GetProperty("termsUrl").GetString());
        Assert.Equal("http://localhost/legal/delete-account", body.GetProperty("deleteAccountUrl").GetString());

        // The URLs the app is given answer.
        foreach (var name in new[] { "privacyUrl", "termsUrl", "deleteAccountUrl" })
        {
            using var page = await anonymous.GetAsync(new Uri(body.GetProperty(name).GetString()!).PathAndQuery, Ct);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }
    }

    [Fact]
    public async Task AConfiguredVersionAndPublicAddressAreUsed()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Legal:Version"] = "2027-03", ["Legal:PublicBaseUrl"] = "https://api.kombin.test/" })));
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/legal", Ct);

        Assert.Equal("2027-03", body.GetProperty("version").GetString());
        Assert.Equal("https://api.kombin.test/legal/privacy", body.GetProperty("privacyUrl").GetString());
        Assert.Equal("https://api.kombin.test/legal/terms", body.GetProperty("termsUrl").GetString());
        Assert.Contains("Sürüm 2027-03", await PageAsync(client, "/legal/terms"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAdminPagesAreStillBehindTheAdminSignIn()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/admin/users", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    private static int Markers(string html) => html.Split("<!-- LAWYER -->").Length - 1;
}
