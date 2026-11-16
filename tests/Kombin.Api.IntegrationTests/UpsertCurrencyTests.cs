using System.Net;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>Night review item 9: prices are Turkish lira only; another currency is refused at the door, never stored as if it were lira.</summary>
public sealed class UpsertCurrencyTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Item(string externalId, string brand, string? currency) => new
    {
        externalId,
        url = $"https://shop.example.com/p/{externalId}",
        title = $"{brand} Blazer",
        price = 100m,
        currency,
        stockStatus = "InStock",
        availableSizes = new[] { "M" },
        product = new { brandName = brand, name = "Blazer", categorySlug = "ust-giyim/blazer", audience = "Unisex", primaryColor = "Green", formality = 3 },
    };

    [Fact]
    public async Task AnOfferInDollarsIsRefusedAndNothingIsStored()
    {
        using var client = factory.CreateClient();
        var brand = Unique("Usd");
        var externalId = $"usd-{Guid.NewGuid():N}";

        using var response = await client.UpsertAsync($"{Slug(brand)}-store", [Item(externalId, brand, "USD")], cancellationToken: Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        Assert.Contains("TRY", text, StringComparison.Ordinal);
        Assert.False(await factory.WithDbAsync(db => db.Offers.AnyAsync(offer => offer.ExternalId == externalId, Ct)));
    }

    [Theory]
    [InlineData(null, "TRY")]
    [InlineData("TRY", "TRY")]
    [InlineData("try", "TRY")]
    public async Task LiraOrNoCurrencyIsStoredAsTry(string? sent, string stored)
    {
        using var client = factory.CreateClient();
        var brand = Unique("Try");
        var externalId = $"try-{Guid.NewGuid():N}";

        var body = await client.UpsertOkAsync($"{Slug(brand)}-store", [Item(externalId, brand, sent)], Ct);

        Assert.Equal(1, body.GetProperty("created").GetInt32());
        Assert.Equal(stored, await factory.WithDbAsync(db => db.Offers.Where(offer => offer.ExternalId == externalId).Select(offer => offer.Currency).FirstAsync(Ct)));
    }
}
