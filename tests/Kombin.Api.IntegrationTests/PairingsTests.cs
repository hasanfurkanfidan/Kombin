using System.Net;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Catalog;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;
using static Kombin.Api.IntegrationTests.IngestionClientExtensions;
using static Kombin.Api.IntegrationTests.ProfileClientExtensions;
using static Kombin.Api.IntegrationTests.WardrobeClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>"What goes with this": the pairings of a wardrobe item and the grid's <c>companionCount</c>.</summary>
public sealed class PairingsTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(Guid itemId, string query = "") => $"{Base}/items/{itemId}/pairings{query}";

    private static List<JsonElement> Items(JsonElement body, string slot) =>
        [.. Group(body, slot).GetProperty("items").EnumerateArray()];

    private static JsonElement Group(JsonElement body, string slot) =>
        body.GetProperty("companions").EnumerateArray().Single(group => group.GetProperty("slot").GetString() == slot);

    private static List<string?> Names(JsonElement body, string slot) =>
        [.. Items(body, slot).Select(companion => companion.GetProperty("item").GetProperty("name").GetString())];

    private static int CompanionTotal(JsonElement body) =>
        body.GetProperty("companions").EnumerateArray().Sum(group => group.GetProperty("total").GetInt32());

    private static async Task<int> PossibleOutfitsAsync(HttpClient client) =>
        (await client.GetJsonAsync($"{Base}/stats", Ct)).GetProperty("possibleOutfits").GetInt32();

    [Fact]
    public async Task ANavyBlazerGoesWithTheShirtAndTheTrousersButNotWithAnotherLayer()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("navy blazer", "ust-giyim/blazer", ProductColor.Navy, 4),
                new("white shirt", "ust-giyim/gomlek", ProductColor.White, 4),
                new("white tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("grey trousers", "alt-giyim/pantolon", ProductColor.Grey, 4),
                new("black loafers", "ayakkabi/loafer", ProductColor.Black, 4),
                new("oversized coat", "dis-giyim/kaban", ProductColor.Beige, 4, Fit: ProductFit.Oversized),
            ],
            Ct);

        var body = await user.Client.GetJsonAsync(Url(ids["navy blazer"]), Ct);

        Assert.Equal(ids["navy blazer"], body.GetProperty("itemId").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("personaId").ValueKind);
        Assert.Equal(["Top", "Bottom", "Shoes", "Accessory"], body.GetProperty("companions").EnumerateArray().Select(group => group.GetProperty("slot").GetString()));

        // The tee is too casual for the blazer, and the coat is another layer.
        Assert.Equal(["white shirt"], Names(body, "Top"));
        Assert.Equal(["grey trousers"], Names(body, "Bottom"));
        Assert.Equal(["black loafers"], Names(body, "Shoes"));
        Assert.Empty(Items(body, "Accessory"));

        var shirt = Items(body, "Top")[0];
        Assert.Equal(91, shirt.GetProperty("score").GetInt32());
        Assert.Equal("neutral", shirt.GetProperty("reason").GetString());
        Assert.Equal(ids["white shirt"], shirt.GetProperty("item").GetProperty("id").GetGuid());

        // The one core outfit the blazer can be worn over.
        Assert.Equal(1, body.GetProperty("outfitCount").GetInt32());
    }

    [Fact]
    public async Task TheLimitIsPerSlotAndAPersonaIsNamed()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var personaId = await user.Client.CreatePersonaAsync("Günlük", cancellationToken: Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("jeans", "alt-giyim/jean", ProductColor.Blue, 2, Fit: ProductFit.Slim),
                new("chinos", "alt-giyim/pantolon", ProductColor.Beige, 2),
            ],
            Ct);

        var body = await user.Client.GetJsonAsync(Url(ids["tee"], "?limit=1"), Ct);

        Assert.Equal(personaId, body.GetProperty("personaId").GetGuid());
        Assert.Single(Items(body, "Bottom"));
        Assert.Equal(2, Group(body, "Bottom").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task OnlyAWearableItemOfTheCallerHasPairings()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateClient();
        var ids = await factory.InsertWardrobeAsync(user.Id, [new("tee", "ust-giyim/tisort", ProductColor.White, 2), new("gone", "ust-giyim/tisort", ProductColor.Black, 2)], Ct);

        using (var response = await anonymous.GetAsync(Url(ids["tee"]), Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var response = await stranger.Client.GetAsync(Url(ids["tee"]), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Recommendation.WardrobeItemNotFound", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using (var delete = await user.Client.DeleteAsync($"{Base}/items/{ids["gone"]}", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using (var response = await user.Client.GetAsync(Url(ids["gone"]), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var unfinished = (await user.Client.CreateUploadUrlAsync(cancellationToken: Ct)).GetProperty("itemId").GetGuid();
        using (var response = await user.Client.GetAsync(Url(unfinished), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=21")]
    public async Task AnOutOfRangeLimitIsRejected(string query)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(user.Id, [new("tee", "ust-giyim/tisort", ProductColor.White, 2)], Ct);

        using var response = await user.Client.GetAsync(Url(ids["tee"], query), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SomeoneElsesPersonaIsNotFound()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        var foreign = await stranger.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        var ids = await factory.InsertWardrobeAsync(user.Id, [new("tee", "ust-giyim/tisort", ProductColor.White, 2)], Ct);

        using var response = await user.Client.GetAsync(Url(ids["tee"], $"?personaId={foreign}"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Profile.PersonaNotFound", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task OutfitCountsAddUpToThePossibleOutfitsAndFollowTheWardrobe()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("black tee", "ust-giyim/tisort", ProductColor.Black, 2),
                new("jeans", "alt-giyim/jean", ProductColor.Blue, 2),
                new("chinos", "alt-giyim/pantolon", ProductColor.Beige, 2),
                new("sneakers", "ayakkabi/sneaker", ProductColor.White, 2),
                new("suit trousers", "alt-giyim/pantolon", ProductColor.Grey, 5),
            ],
            Ct);

        async Task<int> OutfitsOfAsync(string name) =>
            (await user.Client.GetJsonAsync(Url(ids[name]), Ct)).GetProperty("outfitCount").GetInt32();

        Assert.Equal(4, await PossibleOutfitsAsync(user.Client));
        Assert.Equal(2, await OutfitsOfAsync("white tee"));
        Assert.Equal(2, await OutfitsOfAsync("black tee"));
        Assert.Equal(2, await OutfitsOfAsync("jeans"));
        Assert.Equal(2, await OutfitsOfAsync("chinos"));
        Assert.Equal(4, await OutfitsOfAsync("sneakers"));
        Assert.Equal(0, await OutfitsOfAsync("suit trousers"));

        // A new piece changes the fingerprint, so the cached number is not served again.
        var added = await factory.InsertWardrobeAsync(user.Id, [new("black sneakers", "ayakkabi/sneaker", ProductColor.Black, 2)], Ct);
        ids["black sneakers"] = added["black sneakers"];

        Assert.Equal(8, await PossibleOutfitsAsync(user.Client));
        Assert.Equal(4, await OutfitsOfAsync("white tee"));
        Assert.Equal(4, await OutfitsOfAsync("black sneakers"));
    }

    [Fact]
    public async Task TheUnlockFillsTheEmptiestSlotWithinTheBudget()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Bütçe", new { budgetMin = 1231m, budgetMax = 1239m, formalityMin = 2, formalityMax = 4 }, Ct);
        await user.Client.SwipeAsync(persona, CardsWithColor("Black").Take(12).Select(card => (card, "Like")), Ct);

        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white shirt", "ust-giyim/gomlek", ProductColor.White, 3),
                new("navy chinos", "alt-giyim/pantolon", ProductColor.Navy, 3),
                new("beige chinos", "alt-giyim/pantolon", ProductColor.Beige, 3),
                new("navy jacket", "dis-giyim/ceket", ProductColor.Navy, 3),
            ],
            Ct);

        var brand = Unique("Pair");
        var seller = $"{Slug(brand)}-store";
        await user.Client.SeedProductAsync("alt-giyim/pantolon", "Black", 3, 1235m, sellerSlug: seller, brand: brand, modelCode: Unique("bottom"), cancellationToken: Ct);
        await user.Client.SeedProductAsync("ayakkabi/loafer", "Black", 3, 2500m, sellerSlug: seller, brand: brand, modelCode: Unique("dear"), cancellationToken: Ct);
        var fits = await user.Client.SeedProductAsync("ayakkabi/loafer", "Black", 3, 1235m, sellerSlug: seller, brand: brand, modelCode: Unique("fits"), cancellationToken: Ct);

        var body = await user.Client.GetJsonAsync(Url(ids["white shirt"], $"?personaId={persona}"), Ct);

        var unlock = body.GetProperty("unlock");
        Assert.Equal("Shoes", unlock.GetProperty("slot").GetString());
        Assert.Equal(fits, unlock.GetProperty("productId").GetGuid());
        Assert.Equal("Black", unlock.GetProperty("color").GetString());
        Assert.Equal(1235m, unlock.GetProperty("lowestPrice").GetDecimal());
        Assert.Equal(seller, unlock.GetProperty("cheapestSeller").GetProperty("slug").GetString());
        Assert.StartsWith("/api/v1/go/", unlock.GetProperty("goPath").GetString(), StringComparison.Ordinal);
        Assert.True(unlock.GetProperty("matchScore").GetInt32() >= 60);
        Assert.False(unlock.TryGetProperty("url", out _));

        // The shirt with either pair of chinos and the new loafers.
        Assert.Equal(2, unlock.GetProperty("unlocksEstimate").GetInt32());
    }

    [Fact]
    public async Task TheUnlocksPriceSellerAndLinkAreOneOffer()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Kargo", new { budgetMin = 1311m, budgetMax = 1420m }, Ct);
        await user.Client.SwipeAsync(persona, CardsWithColor("Black").Take(12).Select(card => (card, "Like")), Ct);
        var ids = await factory.InsertWardrobeAsync(user.Id, [new("white shirt", "ust-giyim/gomlek", ProductColor.White, 3)], Ct);

        // The first seller lists lower, but its shipping makes the second one cheaper.
        var brand = Unique("Pair");
        var modelCode = Unique("model");
        var (dearSeller, _) = await user.Client.CreateSellerAsync("Dear", Ct);
        var (cheapSeller, _) = await user.Client.CreateSellerAsync("Cheap", Ct);
        var product = await user.Client.SeedProductAsync("alt-giyim/pantolon", "Black", 3, 1320m, sellerSlug: dearSeller, brand: brand, modelCode: modelCode, shippingCost: 90m, cancellationToken: Ct);
        await user.Client.SeedProductAsync("alt-giyim/pantolon", "Black", 3, 1340m, sellerSlug: cheapSeller, brand: brand, modelCode: modelCode, cancellationToken: Ct);

        var unlock = (await user.Client.GetJsonAsync(Url(ids["white shirt"], $"?personaId={persona}"), Ct)).GetProperty("unlock");

        Assert.Equal(product, unlock.GetProperty("productId").GetGuid());
        Assert.Equal(1340m, unlock.GetProperty("lowestPrice").GetDecimal());
        Assert.Equal(cheapSeller, unlock.GetProperty("cheapestSeller").GetProperty("slug").GetString());

        var offers = (await user.Client.GetJsonAsync($"/api/v1/catalog/products/{product}", Ct)).GetProperty("offers").EnumerateArray().ToList();
        Assert.Equal(offers[0].GetProperty("goPath").GetString(), unlock.GetProperty("goPath").GetString());
    }

    [Fact]
    public async Task WithoutAPersonaTheUnlockHasNoMatchScoreAndAnOwnedProductIsNotSuggested()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(user.Id, [new("white shirt", "ust-giyim/gomlek", ProductColor.White, 3)], Ct);

        await user.Client.SeedProductAsync("alt-giyim/pantolon", "Black", 3, 900m, cancellationToken: Ct);

        var unlock = (await user.Client.GetJsonAsync(Url(ids["white shirt"]), Ct)).GetProperty("unlock");

        Assert.False(unlock.TryGetProperty("matchScore", out _));

        var suggested = unlock.GetProperty("productId").GetGuid();
        using (var added = await user.Client.FromProductAsync(suggested, cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        var next = (await user.Client.GetJsonAsync(Url(ids["white shirt"]), Ct)).GetProperty("unlock");
        Assert.True(next.ValueKind == JsonValueKind.Null || next.GetProperty("productId").GetGuid() != suggested);
    }

    [Fact]
    public async Task ThreeCompanionsInEverySlotLeaveNothingToUnlock()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white shirt", "ust-giyim/gomlek", ProductColor.White, 3),
                new("navy chinos", "alt-giyim/pantolon", ProductColor.Navy, 3),
                new("beige chinos", "alt-giyim/pantolon", ProductColor.Beige, 3),
                new("black trousers", "alt-giyim/pantolon", ProductColor.Black, 3),
                new("brown boots", "ayakkabi/bot", ProductColor.Brown, 3),
                new("black boots", "ayakkabi/bot", ProductColor.Black, 3),
                new("brown loafers", "ayakkabi/loafer", ProductColor.Brown, 3),
                new("navy jacket", "dis-giyim/ceket", ProductColor.Navy, 3),
                new("black jacket", "dis-giyim/ceket", ProductColor.Black, 3),
                new("beige coat", "dis-giyim/kaban", ProductColor.Beige, 3),
            ],
            Ct);

        var body = await user.Client.GetJsonAsync(Url(ids["white shirt"]), Ct);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("unlock").ValueKind);
    }

    [Fact]
    public async Task TheGridShowsHowManyPiecesEachItemGoesWith()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("navy blazer", "ust-giyim/blazer", ProductColor.Navy, 4),
                new("white shirt", "ust-giyim/gomlek", ProductColor.White, 4),
                new("grey trousers", "alt-giyim/pantolon", ProductColor.Grey, 4),
                new("black loafers", "ayakkabi/loafer", ProductColor.Black, 4),
                new("printed tee", "ust-giyim/tisort", ProductColor.Multi, 1, ProductPattern.Printed),
            ],
            Ct);

        async Task<Dictionary<Guid, int>> GridAsync(string query = "") =>
            (await user.Client.GetJsonAsync($"{Base}/items{query}", Ct)).GetProperty("items").EnumerateArray()
                .ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("companionCount").GetInt32());

        var grid = await GridAsync();

        Assert.Equal(5, grid.Count);
        Assert.Equal(3, grid[ids["navy blazer"]]);
        Assert.Equal(0, grid[ids["printed tee"]]);
        foreach (var (id, count) in grid)
        {
            Assert.Equal(CompanionTotal(await user.Client.GetJsonAsync(Url(id), Ct)), count);
        }

        // The stats' lists are summaries too, but the count belongs to the grid.
        var stats = await user.Client.GetJsonAsync($"{Base}/stats", Ct);
        Assert.All(stats.GetProperty("leastWorn").EnumerateArray(), item => Assert.False(item.TryGetProperty("companionCount", out _)));

        // A new piece invalidates the cached counts; a removed one leaves the wardrobe and pairs with nothing.
        await factory.InsertWardrobeAsync(user.Id, [new("black trousers", "alt-giyim/pantolon", ProductColor.Black, 4)], Ct);
        Assert.Equal(4, (await GridAsync())[ids["navy blazer"]]);

        using (var delete = await user.Client.DeleteAsync($"{Base}/items/{ids["white shirt"]}", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        Assert.Equal(3, (await GridAsync())[ids["navy blazer"]]);
        Assert.Equal(0, (await GridAsync("?archived=true"))[ids["white shirt"]]);
    }

    [Fact]
    public async Task AnUnfinishedItemInTheGridHasNoCompanions()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var uncategorised = await factory.AddPhotoItemAsync(user.Client, "unsure", Ct);
        await factory.InsertWardrobeAsync(user.Id, [new("tee", "ust-giyim/tisort", ProductColor.White, 2)], Ct);

        var items = (await user.Client.GetJsonAsync($"{Base}/items", Ct)).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(0, items.Single(item => item.GetProperty("id").GetGuid() == uncategorised).GetProperty("companionCount").GetInt32());
    }
}
