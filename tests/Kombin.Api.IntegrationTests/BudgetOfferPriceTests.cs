using System.Net.Http.Json;
using System.Text.Json;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;
using static Kombin.Api.IntegrationTests.TripClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>Night review item 1: the price of a budget offer includes shipping, so its crossed-out price has to as well.</summary>
public sealed class BudgetOfferPriceTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One product at a price (100), a crossed-out list price (120) and shipping (29.99) under a new brand; returns the brand.</summary>
    private static async Task<string> SeedAsync(HttpClient client, string category, string color, decimal shipping, decimal? freeShippingThreshold = null)
    {
        var brand = Unique("Shp");
        var externalId = $"shp-{Guid.NewGuid():N}";
        var item = new
        {
            externalId,
            url = $"https://shop.example.com/p/{externalId}",
            title = $"{brand} {category}",
            price = 100m,
            originalPrice = 120m,
            shippingCost = shipping,
            freeShippingThreshold,
            stockStatus = "InStock",
            availableSizes = new[] { "M" },
            imageUrls = new[] { $"https://img.example.com/{externalId}.jpg" },
            product = new { brandName = brand, name = $"Parça {category[(category.IndexOf('/') + 1)..]}", categorySlug = category, audience = "Unisex", primaryColor = color, formality = 2 },
        };
        await client.UpsertOkAsync($"{Slug(brand)}-store", [item], Ct);

        return brand;
    }

    [Fact]
    public async Task TheCrossedOutPriceIsOnTheSameBasisAsThePriceAndTheShippingIsStated()
    {
        using var anonymous = factory.CreateAnonymousClient();

        // A full neutral capsule, cheaper in total than any other test's: with equal traits the lower total wins the tie, so these are the pieces bought.
        var brands = new[]
        {
            await SeedAsync(anonymous, "ust-giyim/tisort", "White", 29.99m),
            await SeedAsync(anonymous, "alt-giyim/pantolon", "Black", 29.99m),
            await SeedAsync(anonymous, "ayakkabi/sneaker", "White", 29.99m),
            await SeedAsync(anonymous, "dis-giyim/ceket", "Navy", 29.99m),
        };

        var body = await anonymous.BudgetOkAsync(new { budget = 3000, audience = "Unisex", count = 5 }, Ct, anonymous: true);
        var offers = body.GetProperty("outfits").EnumerateArray()
            .SelectMany(outfit => outfit.GetProperty("pieces").EnumerateArray())
            .Where(piece => piece.GetProperty("source").GetString() == "Catalog" && brands.Any(brand => piece.GetProperty("name").GetString()!.Contains(brand, StringComparison.Ordinal)))
            .Select(piece => piece.GetProperty("offer"))
            .ToList();

        Assert.NotEmpty(offers);
        Assert.All(offers, offer =>
        {
            // 100 + 29.99 shipping is the price; the crossed-out 120 is a list price without shipping, so on the price's basis it is 149.99.
            Assert.Equal(129.99m, offer.GetProperty("price").GetDecimal());
            Assert.Equal(149.99m, offer.GetProperty("originalPrice").GetDecimal());
            Assert.True(offer.GetProperty("originalPrice").GetDecimal() > offer.GetProperty("price").GetDecimal());
            Assert.Equal(29.99m, offer.GetProperty("shippingCost").GetDecimal());
        });

        // The outfit's savings stay what the seller shows: 120 - 100 per piece.
        var withMine = body.GetProperty("outfits").EnumerateArray().First(outfit => outfit.GetProperty("pieces").EnumerateArray().Any(piece =>
            piece.GetProperty("source").GetString() == "Catalog" && brands.Any(brand => piece.GetProperty("name").GetString()!.Contains(brand, StringComparison.Ordinal))));
        var mine = withMine.GetProperty("pieces").EnumerateArray().Count(piece => piece.GetProperty("source").GetString() == "Catalog" && brands.Any(brand => piece.GetProperty("name").GetString()!.Contains(brand, StringComparison.Ordinal)));
        Assert.True(withMine.GetProperty("savings").GetDecimal() >= 20m * mine);
    }

    [Fact]
    public async Task FreeShippingIsAShippingCostOfZeroAndTheListPriceStaysAsItIs()
    {
        using var anonymous = factory.CreateAnonymousClient();
        var brands = new[]
        {
            await SeedAsync(anonymous, "ust-giyim/tisort", "White", 29.99m, freeShippingThreshold: 50m),
            await SeedAsync(anonymous, "alt-giyim/pantolon", "Black", 29.99m, freeShippingThreshold: 50m),
            await SeedAsync(anonymous, "ayakkabi/sneaker", "White", 29.99m, freeShippingThreshold: 50m),
            await SeedAsync(anonymous, "dis-giyim/ceket", "Navy", 29.99m, freeShippingThreshold: 50m),
        };

        var body = await anonymous.BudgetOkAsync(new { budget = 3000, audience = "Unisex", count = 5 }, Ct, anonymous: true);
        var offers = body.GetProperty("outfits").EnumerateArray()
            .SelectMany(outfit => outfit.GetProperty("pieces").EnumerateArray())
            .Where(piece => piece.GetProperty("source").GetString() == "Catalog" && brands.Any(brand => piece.GetProperty("name").GetString()!.Contains(brand, StringComparison.Ordinal)))
            .Select(piece => piece.GetProperty("offer"))
            .ToList();

        Assert.NotEmpty(offers);
        Assert.All(offers, offer =>
        {
            Assert.Equal(100m, offer.GetProperty("price").GetDecimal());
            Assert.Equal(120m, offer.GetProperty("originalPrice").GetDecimal());
            Assert.Equal(0m, offer.GetProperty("shippingCost").GetDecimal());
        });
    }
}
