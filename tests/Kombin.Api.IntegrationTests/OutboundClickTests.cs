using System.Net;
using System.Text.Json;
using Kombin.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class OutboundClickTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAnonymousClickIsRecordedAndRedirectedWithTheDefaultTracking()
    {
        using var client = factory.CreateAnonymousClient();
        var product = await factory.SeedProductAsync(client, 500m, cancellationToken: Ct);

        using var response = await client.GetAsync($"/api/v1/go/{product.OfferId}?src=product_detail", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.Contains("X-Correlation-ID"));

        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);

        var click = await SingleClickAsync(product.OfferId);
        Assert.Null(click.UserId);
        Assert.Equal(product.ProductId, click.ProductId);
        Assert.Equal("product_detail", click.Source);
        Assert.Equal(location, click.RedirectUrl);
        Assert.Equal(
            $"https://shop.example.com/p/{product.ExternalId}?utm_source=kombin&utm_medium=app&utm_campaign=product_detail&kombin_click={click.Id}",
            location);
        Assert.False(string.IsNullOrEmpty(click.CorrelationId));
    }

    [Fact]
    public async Task ASignedInClickCarriesTheUserAndThePersona()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        var persona = Guid.NewGuid();
        user.Client.DefaultRequestHeaders.UserAgent.ParseAdd("KombinTest/1.0");

        using var response = await user.Client.GetAsync($"/api/v1/go/{product.OfferId}?src=outfit&personaId={persona}", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var click = await SingleClickAsync(product.OfferId);
        Assert.Equal(user.Id, click.UserId);
        Assert.Equal("outfit", click.Source);
        Assert.Equal(persona, click.PersonaId);
        Assert.Equal("KombinTest/1.0", click.UserAgent);
        Assert.Contains($"kombin_click={click.Id}", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidTokenIsTreatedAsAnonymousNotAsAnError()
    {
        using var seeder = factory.CreateAnonymousClient();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        var product = await factory.SeedProductAsync(seeder, 500m, cancellationToken: Ct);

        using var response = await client.GetAsync($"/api/v1/go/{product.OfferId}", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Null((await SingleClickAsync(product.OfferId)).UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?src=nonsense")]
    [InlineData("?src=PRODUCT_DETAIL&personaId=not-a-guid")]
    public async Task UnknownSourcesAreRecordedAsUnknownAndABadPersonaDoesNotBlockTheRedirect(string query)
    {
        using var client = factory.CreateAnonymousClient();
        var product = await factory.SeedProductAsync(client, 500m, cancellationToken: Ct);

        using var response = await client.GetAsync($"/api/v1/go/{product.OfferId}{query}", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var click = await SingleClickAsync(product.OfferId);
        Assert.Equal(query.Contains("PRODUCT_DETAIL", StringComparison.Ordinal) ? "product_detail" : "unknown", click.Source);
        Assert.Null(click.PersonaId);
    }

    [Fact]
    public async Task AnInactiveOfferIsNotRedirectedToAndRecordsNothing()
    {
        using var client = factory.CreateAnonymousClient();
        var product = await factory.SeedProductAsync(client, 500m, cancellationToken: Ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            await db.Offers.Where(offer => offer.Id == product.OfferId).ExecuteUpdateAsync(set => set.SetProperty(offer => offer.IsActive, false), Ct);
        }

        using var response = await client.GetAsync($"/api/v1/go/{product.OfferId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Engagement.OfferNotAvailable", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Null(response.Headers.Location);

        await using var verify = factory.Services.CreateAsyncScope();
        Assert.False(await verify.ServiceProvider.GetRequiredService<KombinDbContext>().Clicks.AnyAsync(click => click.OfferId == product.OfferId, Ct));
    }

    [Fact]
    public async Task AnUnknownOfferIsNotFound()
    {
        using var client = factory.CreateAnonymousClient();

        using var response = await client.GetAsync($"/api/v1/go/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Engagement.OfferNotAvailable", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ASellerTemplateWithItsParametersBuildsTheRedirect()
    {
        using var client = factory.CreateAnonymousClient();
        var product = await factory.SeedProductAsync(client, 500m, cancellationToken: Ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            var seller = await db.Sellers.SingleAsync(candidate => candidate.Slug == product.SellerSlug, Ct);
            seller.AffiliateUrlTemplate = "https://tracking.example/redirect?url={url}&subid={clickId}&aff={p.affId}&src={source}";
            seller.AffiliateParams = new Dictionary<string, string> { ["affId"] = "kombin-77" };
            await db.SaveChangesAsync(Ct);
        }

        using var response = await client.GetAsync($"/api/v1/go/{product.OfferId}?src=saved", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var click = await SingleClickAsync(product.OfferId);
        Assert.Equal(
            $"https://tracking.example/redirect?url={Uri.EscapeDataString($"https://shop.example.com/p/{product.ExternalId}")}&subid={click.Id}&aff=kombin-77&src=saved",
            response.Headers.Location?.OriginalString);
        Assert.Equal(response.Headers.Location?.OriginalString, click.RedirectUrl);
    }

    [Fact]
    public async Task EveryClickGetsItsOwnId()
    {
        using var client = factory.CreateAnonymousClient();
        var product = await factory.SeedProductAsync(client, 500m, cancellationToken: Ct);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await client.GetAsync($"/api/v1/go/{product.OfferId}", Ct);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var clicks = await scope.ServiceProvider.GetRequiredService<KombinDbContext>().Clicks
            .Where(click => click.OfferId == product.OfferId)
            .ToListAsync(Ct);

        Assert.Equal(3, clicks.Select(click => click.Id).Distinct().Count());
        Assert.All(clicks, click => Assert.Contains($"kombin_click={click.Id}", click.RedirectUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheClickHistoryListsOnlyTheCallersOwnSignedInClicksNewestFirst()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var first = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        var second = await factory.SeedProductAsync(user.Client, 400m, cancellationToken: Ct);

        using var firstClick = await user.Client.GetAsync($"/api/v1/go/{first.OfferId}?src=saved", Ct);
        using var secondClick = await user.Client.GetAsync($"/api/v1/go/{second.OfferId}?src=alert", Ct);
        using var othersClick = await other.Client.GetAsync($"/api/v1/go/{first.OfferId}", Ct);
        using var anonymousClick = await anonymous.GetAsync($"/api/v1/go/{first.OfferId}", Ct);

        var history = await user.Client.GetJsonAsync("/api/v1/me/clicks", Ct);

        Assert.Equal(2, history.GetProperty("totalCount").GetInt32());
        var items = history.GetProperty("items");
        Assert.Equal(second.OfferId, items[0].GetProperty("offerId").GetGuid());
        Assert.Equal("alert", items[0].GetProperty("source").GetString());
        Assert.Equal(second.ProductId, items[0].GetProperty("product").GetProperty("id").GetGuid());
        Assert.Equal(second.SellerSlug, items[0].GetProperty("seller").GetProperty("slug").GetString());
        Assert.Equal(first.OfferId, items[1].GetProperty("offerId").GetGuid());
        Assert.Equal(JsonValueKind.String, items[1].GetProperty("clickedAt").ValueKind);

        Assert.Equal(1, (await other.Client.GetJsonAsync("/api/v1/me/clicks", Ct)).GetProperty("totalCount").GetInt32());
    }

    private async Task<Kombin.Api.Persistence.Entities.Engagement.Click> SingleClickAsync(Guid offerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

        return await db.Clicks.AsNoTracking().SingleAsync(click => click.OfferId == offerId, Ct);
    }
}
