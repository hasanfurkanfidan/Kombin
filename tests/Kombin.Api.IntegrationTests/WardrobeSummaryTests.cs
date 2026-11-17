using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;
using static Kombin.Api.IntegrationTests.ProfileClientExtensions;
using static Kombin.Api.IntegrationTests.WardrobeClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary><c>GET /me/wardrobe/summary</c>: the wardrobe's value, the cost per wear and "Giymediklerin".</summary>
public sealed class WardrobeSummaryTests(ApiFactory factory)
{
    private const string Summary = "/api/v1/me/wardrobe/summary";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> SetPriceAsync(Guid itemId, decimal? price) =>
        factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == itemId).ExecuteUpdateAsync(set => set.SetProperty(item => item.PurchasePrice, price), Ct));

    private Task<int> AgeAsync(IEnumerable<Guid> itemIds, int days)
    {
        var ids = itemIds.ToList();
        return factory.WithDbAsync(db => db.WardrobeItems.Where(item => ids.Contains(item.Id)).ExecuteUpdateAsync(set => set.SetProperty(item => item.CreatedAt, TestClock.UtcNow.AddDays(-days)), Ct));
    }

    private static string[] Names(JsonElement section) =>
        [.. section.GetProperty("items").EnumerateArray().Select(unworn => unworn.GetProperty("item").GetProperty("name").GetString()!)];

    private static JsonElement Unworn(JsonElement summary, string name) =>
        summary.GetProperty("unworn").GetProperty("items").EnumerateArray().Single(item => item.GetProperty("item").GetProperty("name").GetString() == name);

    private static string? Reason(JsonElement unworn) =>
        unworn.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : unworn.GetProperty("reason").GetString();

    private async Task InsertOutfitAsync(Guid userId, OutfitStatus status, params Guid[] itemIds) =>
        await factory.WithDbAsync(async db =>
        {
            var outfit = new Outfit
            {
                UserId = userId,
                Kind = OutfitKind.Daily,
                Completeness = OutfitCompleteness.Complete,
                ForDate = new DateOnly(2026, 1, 1),
                Rule = "summary",
                Hash = Guid.NewGuid().ToString("N"),
                Status = status,
                Narration = "Özet",
                NarrationInput = "{}",
                SavedAt = status == OutfitStatus.Saved ? TestClock.UtcNow : null,
                DismissedAt = status == OutfitStatus.Dismissed ? TestClock.UtcNow : null,
            };
            outfit.Items.AddRange(itemIds.Select((id, position) => new OutfitItem { Slot = OutfitSlot.Top, Role = OutfitItemRole.Wardrobe, WardrobeItemId = id, Position = position }));
            db.Outfits.Add(outfit);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    [Fact]
    public async Task AnEmptyOrTooSmallWardrobeGetsAPlainMessageAndNoNumbers()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var empty = await user.Client.GetJsonAsync(Summary, Ct);
        Assert.Equal(0, empty.GetProperty("pieceCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(empty.GetProperty("message").GetString()));
        Assert.False(empty.TryGetProperty("value", out _));
        Assert.False(empty.TryGetProperty("costPerWear", out _));
        Assert.False(empty.TryGetProperty("unworn", out _));

        await factory.InsertWardrobeAsync(user.Id, RecommendationClientExtensions.FortyPieces.Take(WardrobeSummaryRules.MinPieces - 1), Ct);
        var few = await user.Client.GetJsonAsync(Summary, Ct);
        Assert.Equal(WardrobeSummaryRules.MinPieces - 1, few.GetProperty("pieceCount").GetInt32());
        Assert.Contains((WardrobeSummaryRules.MinPieces - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), few.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(few.TryGetProperty("value", out _));

        await factory.InsertWardrobeAsync(user.Id, RecommendationClientExtensions.FortyPieces.Skip(20).Take(1), Ct);
        var enough = await user.Client.GetJsonAsync(Summary, Ct);
        Assert.False(enough.TryGetProperty("message", out _));
        Assert.True(enough.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task APieceThatIsStillBeingPreparedOrWasRemovedIsNotInTheWardrobe()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(user.Id, RecommendationClientExtensions.FortyPieces.Take(WardrobeSummaryRules.MinPieces), Ct);
        await factory.AddPhotoItemAsync(user.Client, "not-garment", Ct);
        using (var removed = await user.Client.DeleteAsync($"/api/v1/me/wardrobe/items/{ids.Values.First()}", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        // Four ready pieces and a photo item that is ready too: five, so numbers appear; the removed one is not counted.
        var summary = await user.Client.GetJsonAsync(Summary, Ct);
        Assert.Equal(WardrobeSummaryRules.MinPieces, summary.GetProperty("pieceCount").GetInt32());
    }

    [Fact]
    public async Task TheValueKeepsOwnPricesEstimatesAndUnpricedPiecesApart()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("black tee", "ust-giyim/tisort", ProductColor.Black, 2),
                new("blue jeans", "alt-giyim/jean", ProductColor.Blue, 2),
                new("white sneakers", "ayakkabi/sneaker", ProductColor.White, 2),
            ],
            Ct);
        await SetPriceAsync(ids["white tee"], 1000m);
        await SetPriceAsync(ids["black tee"], 500m);
        await SetPriceAsync(ids["white sneakers"], 750m);

        // Two catalog pieces: one still sold (its lowest price today is the estimate), one nobody sells any more.
        var sold = await user.Client.SeedProductAsync("ust-giyim/gomlek", "White", 3, 400m, cancellationToken: Ct);
        var gone = await user.Client.SeedProductAsync("alt-giyim/pantolon", "Navy", 3, 600m, cancellationToken: Ct);
        await factory.WithDbAsync(async db =>
        {
            await db.Offers.Where(offer => offer.ProductId == sold).ExecuteUpdateAsync(set => set.SetProperty(offer => offer.CurrentPrice, 350m), Ct);
            return await db.Offers.Where(offer => offer.ProductId == gone).ExecuteUpdateAsync(set => set.SetProperty(offer => offer.StockStatus, StockStatus.OutOfStock), Ct);
        });
        using (var addSold = await user.Client.FromProductAsync(sold, cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Created, addSold.StatusCode);
        }

        using (var addGone = await user.Client.FromProductAsync(gone, cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Created, addGone.StatusCode);
        }

        var value = (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("value");

        Assert.Equal("TRY", value.GetProperty("currency").GetString());
        Assert.Equal(2600m, value.GetProperty("total").GetDecimal());
        Assert.Equal(2250m, value.GetProperty("knownTotal").GetDecimal());
        Assert.Equal(350m, value.GetProperty("estimatedTotal").GetDecimal());
        Assert.True(value.GetProperty("isEstimate").GetBoolean());
        var counts = value.GetProperty("counts");
        Assert.Equal((3, 1, 2), (counts.GetProperty("priced").GetInt32(), counts.GetProperty("estimated").GetInt32(), counts.GetProperty("unpriced").GetInt32()));

        var tops = value.GetProperty("byCategory").EnumerateArray().Single(category => category.GetProperty("slug").GetString() == "ust-giyim");
        Assert.Equal(1850m, tops.GetProperty("total").GetDecimal());
        Assert.Equal(350m, tops.GetProperty("estimatedTotal").GetDecimal());
        Assert.True(tops.GetProperty("isEstimate").GetBoolean());
        Assert.Equal(3, tops.GetProperty("pieceCount").GetInt32());

        var shoes = value.GetProperty("byCategory").EnumerateArray().Single(category => category.GetProperty("slug").GetString() == "ayakkabi");
        Assert.Equal(750m, shoes.GetProperty("total").GetDecimal());
        Assert.False(shoes.GetProperty("isEstimate").GetBoolean());

        // The bottoms hold a priceless jean and a product nobody sells: no total at all, never a zero that looks like a value.
        var bottoms = value.GetProperty("byCategory").EnumerateArray().Single(category => category.GetProperty("slug").GetString() == "alt-giyim");
        Assert.Equal(JsonValueKind.Null, bottoms.GetProperty("total").ValueKind);
        Assert.Equal(2, bottoms.GetProperty("unpricedCount").GetInt32());
    }

    [Fact]
    public async Task AnOwnPriceWinsOverTheCatalogsAndWithNothingPricedThereIsNoTotal()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await user.Client.SeedProductAsync("ust-giyim/gomlek", "White", 3, 400m, cancellationToken: Ct);
        using (var added = await user.Client.FromProductAsync(product, cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        await factory.InsertWardrobeAsync(user.Id, RecommendationClientExtensions.FortyPieces.Take(WardrobeSummaryRules.MinPieces - 1), Ct);

        // Estimated first: the catalog price, flagged.
        var estimated = (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("value");
        Assert.Equal((400m, true), (estimated.GetProperty("total").GetDecimal(), estimated.GetProperty("isEstimate").GetBoolean()));

        // The user's own price replaces it and is a fact.
        var itemId = await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.UserId == user.Id && item.ProductId == product).Select(item => item.Id).SingleAsync(Ct));
        await SetPriceAsync(itemId, 275m);
        var own = (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("value");
        Assert.Equal((275m, false, 275m, 0m), (own.GetProperty("total").GetDecimal(), own.GetProperty("isEstimate").GetBoolean(), own.GetProperty("knownTotal").GetDecimal(), own.GetProperty("estimatedTotal").GetDecimal()));

        // Nothing priced at all: no total, a note instead.
        await SetPriceAsync(itemId, null);
        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == itemId).ExecuteUpdateAsync(set => set.SetProperty(item => item.ProductId, (Guid?)null), Ct));
        var none = (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("value");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("total").ValueKind);
        Assert.False(none.GetProperty("isEstimate").GetBoolean());
        Assert.Equal(WardrobeSummaryCopy.NothingPriced, none.GetProperty("note").GetString());
        Assert.Equal(WardrobeSummaryRules.MinPieces, none.GetProperty("counts").GetProperty("unpriced").GetInt32());
    }

    [Fact]
    public async Task TheCostPerWearIsPricePerWearForTheMostWornPricedPieces()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("black tee", "ust-giyim/tisort", ProductColor.Black, 2),
                new("blue jeans", "alt-giyim/jean", ProductColor.Blue, 2),
                new("white sneakers", "ayakkabi/sneaker", ProductColor.White, 2),
                new("grey runners", "ayakkabi/sneaker", ProductColor.Grey, 1),
            ],
            Ct);
        await SetPriceAsync(ids["white tee"], 900m);
        await SetPriceAsync(ids["black tee"], 400m);
        await SetPriceAsync(ids["blue jeans"], 1200m);

        var today = DateOnly.FromDateTime(TestClock.UtcNow.UtcDateTime);
        async Task WearTimesAsync(string name, int times)
        {
            for (var day = 1; day <= times; day++)
            {
                using var wear = await user.Client.WearAsync(ids[name], today.AddDays(-day), Ct);
                Assert.True(wear.IsSuccessStatusCode);
            }
        }

        await WearTimesAsync("white tee", 3);
        await WearTimesAsync("black tee", 1);
        await WearTimesAsync("white sneakers", 5);

        var entries = (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("costPerWear").EnumerateArray().ToList();

        // The sneakers are the most worn but have no price, the jeans were never worn: neither has a cost per wear.
        Assert.Equal(["white tee", "black tee"], entries.Select(entry => entry.GetProperty("item").GetProperty("name").GetString()));
        Assert.Equal([300m, 400m], entries.Select(entry => entry.GetProperty("costPerWear").GetDecimal()));
        Assert.Equal([3, 1], entries.Select(entry => entry.GetProperty("wearCount").GetInt32()));
        Assert.All(entries, entry => Assert.False(entry.GetProperty("priceIsEstimate").GetBoolean()));
    }

    [Fact]
    public async Task AnEstimatedPriceGivesAnEstimatedCostPerWear()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await user.Client.SeedProductAsync("ust-giyim/gomlek", "White", 3, 350m, cancellationToken: Ct);
        using var added = await user.Client.FromProductAsync(product, cancellationToken: Ct);
        var itemId = (await added.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await factory.InsertWardrobeAsync(user.Id, RecommendationClientExtensions.FortyPieces.Take(WardrobeSummaryRules.MinPieces - 1), Ct);
        var today = DateOnly.FromDateTime(TestClock.UtcNow.UtcDateTime);
        foreach (var day in new[] { 1, 2 })
        {
            using var wear = await user.Client.WearAsync(itemId, today.AddDays(-day), Ct);
            Assert.True(wear.IsSuccessStatusCode);
        }

        var entry = Assert.Single((await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("costPerWear").EnumerateArray());

        Assert.Equal((175m, 350m, true), (entry.GetProperty("costPerWear").GetDecimal(), entry.GetProperty("price").GetDecimal(), entry.GetProperty("priceIsEstimate").GetBoolean()));
    }

    [Fact]
    public async Task GiymediklerinListsOldNeverWornPiecesOutsideSavedAndWornOutfits()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("worn tee", "ust-giyim/tisort", ProductColor.Black, 2),
                new("saved shirt", "ust-giyim/gomlek", ProductColor.White, 3),
                new("dismissed shirt", "ust-giyim/gomlek", ProductColor.Blue, 3),
                new("navy chinos", "alt-giyim/pantolon", ProductColor.Navy, 3),
                new("white sneakers", "ayakkabi/sneaker", ProductColor.White, 2),
                new("new jeans", "alt-giyim/jean", ProductColor.Blue, 2),
            ],
            Ct);

        // Everything but the new jeans was added 40 days ago (30 is the line).
        await AgeAsync(ids.Where(pair => pair.Key != "new jeans").Select(pair => pair.Value), 40);
        await AgeAsync([ids["new jeans"]], 29);
        using (var wear = await user.Client.WearAsync(ids["worn tee"], cancellationToken: Ct))
        {
            Assert.True(wear.IsSuccessStatusCode);
        }

        await InsertOutfitAsync(user.Id, OutfitStatus.Saved, ids["saved shirt"]);
        await InsertOutfitAsync(user.Id, OutfitStatus.Dismissed, ids["dismissed shirt"]);

        var unworn = (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("unworn");

        // The wear and the saved outfit take pieces out; a dismissed suggestion and a young piece do not count as use or as old.
        Assert.Equal(["dismissed shirt", "navy chinos", "white sneakers", "white tee"], Names(unworn).Order(StringComparer.Ordinal));
        Assert.Equal(4, unworn.GetProperty("totalCount").GetInt32());
        Assert.All(unworn.GetProperty("items").EnumerateArray(), item => Assert.InRange(item.GetProperty("daysInWardrobe").GetInt32(), 39, 41));

        // A worn outfit counts as use too.
        await InsertOutfitAsync(user.Id, OutfitStatus.Worn, ids["navy chinos"]);
        Assert.DoesNotContain("navy chinos", Names((await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("unworn")));

        // Old enough now: the jeans join, at exactly 30 days.
        await AgeAsync([ids["new jeans"]], 30);
        Assert.Contains("new jeans", Names((await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("unworn")));
    }

    [Fact]
    public async Task AWardrobeWithNothingUnwornSaysSo()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertWardrobeAsync(user.Id, RecommendationClientExtensions.FortyPieces.Take(WardrobeSummaryRules.MinPieces), Ct);

        var unworn = (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("unworn");

        Assert.Equal(0, unworn.GetProperty("totalCount").GetInt32());
        Assert.Empty(unworn.GetProperty("items").EnumerateArray());
        Assert.Equal(WardrobeSummaryCopy.NothingUnworn, unworn.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheReasonIsNoPartnerFirstThenOutOfSeasonThenNone()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(TestClock.IstanbulNoon);

        // The season the piece is not made for: whichever one today is not.
        var current = WeatherRules.SeasonOf(WearRules.Today(TestClock.IstanbulNoon));
        var other = Enum.GetValues<Season>().First(season => season is not Season.None and not Season.AllYear && season != current);

        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("navy chinos", "alt-giyim/pantolon", ProductColor.Navy, 3),
                new("white sneakers", "ayakkabi/sneaker", ProductColor.White, 2),
                new("grey tee", "ust-giyim/tisort", ProductColor.Grey, 2, Seasons: other),
                new("black tee", "ust-giyim/tisort", ProductColor.Black, 2),
                new("lonely belt", "aksesuar/kemer", ProductColor.Brown, 3, Seasons: other),
            ],
            Ct);
        await AgeAsync(ids.Values, 60);

        var grid = (await user.Client.GetJsonAsync("/api/v1/me/wardrobe/items", Ct)).GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
        var summary = await user.Client.GetJsonAsync(Summary, Ct);

        // Same number as the grid's companionCount: the pieces that pair with something get no reason in season, "mevsimi değil" out of it.
        Assert.True(grid["white tee"].GetProperty("companionCount").GetInt32() > 0);
        Assert.True(grid["grey tee"].GetProperty("companionCount").GetInt32() > 0);
        Assert.Null(Reason(Unworn(summary, "white tee")));
        Assert.Null(Reason(Unworn(summary, "navy chinos")));
        var outOfSeason = Unworn(summary, "grey tee");
        Assert.Equal(("OutOfSeason", "mevsimi değil"), (Reason(outOfSeason), outOfSeason.GetProperty("reasonText").GetString()));
        Assert.Equal(JsonValueKind.Null, outOfSeason.GetProperty("unlock").ValueKind);
    }

    [Fact]
    public async Task ThePiecesWithNoPartnerSayEsiYokAndTheFirstThreeCarryTheUnlock()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Bütçe", new { budgetMin = 1347.31m, budgetMax = 1347.39m, formalityMin = 1, formalityMax = 5 }, Ct);
        await user.Client.SwipeAsync(persona, CardsWithColor("Black").Take(12).Select(card => (card, "Like")), Ct);

        // Only tops: two tops never pair, so every piece has no partner.
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("tee 1", "ust-giyim/tisort", ProductColor.White, 3),
                new("tee 2", "ust-giyim/tisort", ProductColor.White, 3),
                new("tee 3", "ust-giyim/tisort", ProductColor.White, 3),
                new("tee 4", "ust-giyim/tisort", ProductColor.White, 3),
                new("tee 5", "ust-giyim/tisort", ProductColor.White, 3),
            ],
            Ct);
        var created = TestClock.UtcNow;
        await factory.WithDbAsync(async db =>
        {
            foreach (var (name, index) in ids.Keys.Order(StringComparer.Ordinal).Select((name, index) => (name, index)))
            {
                var id = ids[name];
                await db.WardrobeItems.Where(item => item.Id == id).ExecuteUpdateAsync(set => set.SetProperty(item => item.CreatedAt, created.AddDays(-50 - index)), Ct);
            }

            return 0;
        });

        var brand = Unique("Sum");
        var seller = $"{Slug(brand)}-store";
        var fits = await user.Client.SeedProductAsync("alt-giyim/pantolon", "Black", 3, 1347.35m, sellerSlug: seller, brand: brand, modelCode: Unique("bot"), cancellationToken: Ct);

        var unworn = (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("unworn");
        var items = unworn.GetProperty("items").EnumerateArray().ToList();

        // Oldest first.
        Assert.Equal(["tee 5", "tee 4", "tee 3", "tee 2", "tee 1"], items.Select(item => item.GetProperty("item").GetProperty("name").GetString()));
        Assert.All(items, item => Assert.Equal(("NoPartner", "eşi yok"), (item.GetProperty("reason").GetString(), item.GetProperty("reasonText").GetString())));

        // The suggestion is the pairings route's own, for the first three only (each one is a catalog query).
        Assert.Equal(WardrobeSummaryRules.UnlockLookups, items.Count(item => item.GetProperty("unlock").ValueKind == JsonValueKind.Object));
        Assert.All(items.Take(WardrobeSummaryRules.UnlockLookups), item => Assert.Equal(fits, item.GetProperty("unlock").GetProperty("productId").GetGuid()));
        Assert.All(items.Skip(WardrobeSummaryRules.UnlockLookups), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("unlock").ValueKind));
        var unlock = items[0].GetProperty("unlock");
        Assert.StartsWith("/api/v1/go/", unlock.GetProperty("goPath").GetString(), StringComparison.Ordinal);
        Assert.False(unlock.TryGetProperty("url", out _));

        var pairings = await user.Client.GetJsonAsync($"/api/v1/me/wardrobe/items/{ids["tee 5"]}/pairings", Ct);
        Assert.Equal(pairings.GetProperty("unlock").GetProperty("productId").GetGuid(), unlock.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task TheSummaryIsTheCallersOwnAndNeedsSignIn()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        await factory.InsertWardrobeAsync(user.Id, RecommendationClientExtensions.FortyPieces.Take(WardrobeSummaryRules.MinPieces), Ct);

        Assert.Equal(0, (await stranger.Client.GetJsonAsync(Summary, Ct)).GetProperty("pieceCount").GetInt32());
        Assert.Equal(WardrobeSummaryRules.MinPieces, (await user.Client.GetJsonAsync(Summary, Ct)).GetProperty("pieceCount").GetInt32());
        using var signedOut = await anonymous.GetAsync(Summary, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }
}
