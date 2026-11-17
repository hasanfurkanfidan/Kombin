using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Night review item 1: the price of a budget offer includes shipping, so its crossed-out price has to as well.
/// The budget search reads the whole shared catalog, so which products it buys depends on every other test's data. These tests therefore do not
/// ask the shared search what it chose: they run its real parts (candidate query, engine, picker, answer builder) over the four products they seeded,
/// with a price cap that only a product priced like them can pass (see <see cref="BudgetOfMineAsync"/>).
/// </summary>
public sealed class BudgetOfferPriceTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One product at a price (100), a crossed-out list price (120) and shipping (29.99) under a new brand; returns its product ID.</summary>
    private static async Task<Guid> SeedAsync(HttpClient client, string category, string color, decimal shipping, decimal? freeShippingThreshold = null)
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
        var body = await client.UpsertOkAsync($"{Slug(brand)}-store", [item], Ct);

        return body.GetProperty("results")[0].GetProperty("productId").GetGuid();
    }

    /// <summary>A full neutral capsule (top, bottom, shoes, outer layer), each piece with the same price, list price and shipping.</summary>
    private static async Task<HashSet<Guid>> SeedCapsuleAsync(HttpClient client, decimal shipping, decimal? freeShippingThreshold = null) =>
    [
        await SeedAsync(client, "ust-giyim/tisort", "White", shipping, freeShippingThreshold),
        await SeedAsync(client, "alt-giyim/pantolon", "Black", shipping, freeShippingThreshold),
        await SeedAsync(client, "ayakkabi/sneaker", "White", shipping, freeShippingThreshold),
        await SeedAsync(client, "dis-giyim/ceket", "Navy", shipping, freeShippingThreshold),
    ];

    private sealed record Answer(BudgetCatalog Catalog, BudgetOutfitsResponse Response);

    /// <summary>
    /// The budget answer for the capsule <paramref name="mine"/>, built from the real parts of the search with inputs only these products can fill.
    /// The candidate query (<see cref="BudgetCandidates.LoadAsync"/>) is capped just above the capsule's price, so a foreign product would have to be
    /// priced within a few lira of it and newer than all four to be in the way (the shortlist is the newest 100 per price band); the candidates are then
    /// narrowed to the four ids, and the engine, the picker and the answer builder run on them as <see cref="BudgetOutfits.SearchAsync"/> does.
    /// </summary>
    private Task<Answer> BudgetOfMineAsync(HashSet<Guid> mine, decimal cap) =>
        factory.WithDbAsync(async db =>
        {
            var date = DateOnly.FromDateTime(TestClock.UtcNow.UtcDateTime);
            var ask = new BudgetAsk(3000, null, date, UseWardrobe: false, Seed: 0, Count: 5);
            var caller = BudgetCaller.Anonymous(Audience.Unisex);
            var context = OutfitContext.Create(ask.Occasion, caller.Persona.Taste, WeatherRules.FromSeason(date), date);
            var slots = context.Weather.Outerwear == OuterwearPolicy.Forbidden ? OutfitSlots.Core : [.. OutfitSlots.Core, OutfitSlot.Outerwear];

            var loaded = await BudgetCandidates.LoadAsync(
                db,
                [.. slots.Select(slot => new BudgetQuery(slot, context.BandMin, context.BandMax, caller.Audience, context.AvoidedColors, cap, []))],
                null,
                null,
                Ct);
            var catalog = new BudgetCatalog([.. loaded.Pieces.Where(piece => mine.Contains(piece.Id))], loaded.Offers.Where(pair => mine.Contains(pair.Key)).ToDictionary());

            var shortlist = slots.SelectMany(slot => BudgetSearch.Shortlist(catalog.Pieces.Where(piece => piece.Slot == slot), context)).ToList();
            var acceptable = OutfitGenerator.Generate(new GenerationRequest(
                [], shortlist, context, null, BudgetSearch.MaxGaps, caller.Dismissed, BudgetSearch.CatalogPerSlot, BudgetSearch.Reach(ask.Budget)));
            var pick = BudgetSearch.Pick(acceptable, ask.Budget, ask.Count, ask.Seed);
            var result = new BudgetResult(context, [.. pick.Outfits.Select(outfit => outfit.Candidate)], false, false, false, catalog);
            var narrations = await BudgetOutfits.NarrateAsync(db, result.Outfits, context, TestClock.UtcNow, Ct);

            return new Answer(catalog, await BudgetOutfits.RespondAsync(db, factory.Storage, ask, caller, result, null, narrations, Ct));
        });

    [Fact]
    public async Task TheCrossedOutPriceIsOnTheSameBasisAsThePriceAndTheShippingIsStated()
    {
        using var anonymous = factory.CreateAnonymousClient();
        var mine = await SeedCapsuleAsync(anonymous, 29.99m);

        var (catalog, answer) = await BudgetOfMineAsync(mine, cap: 130m);

        // The candidate query found every product of the capsule, each with the one offer that was seeded for it.
        Assert.Equal(mine.Order(), catalog.Offers.Keys.Order());
        Assert.All(catalog.Offers.Values, offer =>
        {
            Assert.Equal(129.99m, offer.Price);
            Assert.Equal(100m, offer.CurrentPrice);
            Assert.Equal(29.99m, offer.ShippingCost);
            Assert.Equal(149.99m, offer.ListPrice);
        });

        Assert.NotEmpty(answer.Outfits);
        var bought = answer.Outfits.SelectMany(outfit => outfit.Pieces).Where(piece => piece.Source == PieceSource.Catalog).ToList();
        Assert.NotEmpty(bought);
        Assert.All(bought, piece =>
        {
            Assert.Contains(piece.ProductId!.Value, mine);

            // 100 + 29.99 shipping is the price; the crossed-out 120 is a list price without shipping, so on the price's basis it is 149.99.
            var offer = piece.Offer!;
            Assert.Equal(129.99m, offer.Price);
            Assert.Equal(149.99m, offer.OriginalPrice);
            Assert.True(offer.OriginalPrice > offer.Price);
            Assert.Equal(29.99m, offer.ShippingCost);
        });

        // The outfit's savings stay what the seller shows: 120 - 100 per piece.
        Assert.All(answer.Outfits, outfit => Assert.Equal(20m * outfit.Pieces.Count(piece => piece.Source == PieceSource.Catalog), outfit.Savings));
    }

    [Fact]
    public async Task FreeShippingIsAShippingCostOfZeroAndTheListPriceStaysAsItIs()
    {
        using var anonymous = factory.CreateAnonymousClient();
        var mine = await SeedCapsuleAsync(anonymous, 29.99m, freeShippingThreshold: 50m);

        var (catalog, answer) = await BudgetOfMineAsync(mine, cap: 101m);

        Assert.Equal(mine.Order(), catalog.Offers.Keys.Order());
        Assert.All(catalog.Offers.Values, offer =>
        {
            Assert.Equal(100m, offer.Price);
            Assert.Equal(0m, offer.ShippingCost);
            Assert.Equal(120m, offer.ListPrice);
        });

        Assert.NotEmpty(answer.Outfits);
        var bought = answer.Outfits.SelectMany(outfit => outfit.Pieces).Where(piece => piece.Source == PieceSource.Catalog).ToList();
        Assert.NotEmpty(bought);
        Assert.All(bought, piece =>
        {
            Assert.Contains(piece.ProductId!.Value, mine);
            Assert.Equal(100m, piece.Offer!.Price);
            Assert.Equal(120m, piece.Offer.OriginalPrice);
            Assert.Equal(0m, piece.Offer.ShippingCost);
        });
    }
}
