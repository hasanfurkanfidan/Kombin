using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Recommendation.GetDaily;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;
using static Kombin.Api.IntegrationTests.RecommendationClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>The outfit engine end to end: daily, how-to-wear, the empty wardrobe, the outfit actions, and the wardrobe numbers.</summary>
public sealed class RecommendationTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Tomorrow => Kombin.Api.Features.Wardrobe.WearRules.Today(DateTimeOffset.UtcNow).AddDays(1);

    private async Task<(TestUser User, Dictionary<Guid, WardrobeSeed> Seeds)> FortyPieceUserAsync()
    {
        var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(user.Id, FortyPieces, Ct);

        return (user, FortyPieces.ToDictionary(seed => ids[seed.Name]));
    }

    // Catalog products seeded here are black: the weekly-matches tests count every new product their personas like (white, grey, navy,
    // brown, ...) across the shared database, and no persona there likes black.

    // ---- daily ----

    [Fact]
    public async Task ADailyOutfitFromAFortyPieceWardrobeIsWearableAndDressedForTheRain()
    {
        var (user, seeds) = await FortyPieceUserAsync();
        using var _ = user;
        await user.Client.SetCityAsync("Rainville", Ct);

        var body = await user.Client.DailyAsync("?date=tomorrow", Ct);

        Assert.False(body.GetProperty("wardrobeEmpty").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("message").ValueKind);
        var weather = body.GetProperty("weather");
        Assert.Equal("Rainville", weather.GetProperty("city").GetString());
        Assert.True(weather.GetProperty("rain").GetBoolean());
        Assert.Equal("Mild", weather.GetProperty("band").GetString());

        var outfit = body.GetProperty("outfit");
        Assert.Equal("Daily", outfit.GetProperty("kind").GetString());
        Assert.Equal("Complete", outfit.GetProperty("completeness").GetString());
        Assert.Equal("Suggested", outfit.GetProperty("status").GetString());
        Assert.Equal(Tomorrow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), outfit.GetProperty("date").GetString());
        Assert.InRange(outfit.GetProperty("score").GetInt32(), OutfitScoring.AcceptanceThreshold, 100);
        Assert.Equal(JsonValueKind.Null, outfit.GetProperty("completionPrice").ValueKind);

        // Rain: a layer is required, and the line says why the outfit works.
        Assert.Equal("Rain", outfit.GetProperty("rule").GetString());
        Assert.NotNull(outfit.Slot("Outerwear"));
        Assert.All(outfit.Pieces(), piece => Assert.Equal("Wardrobe", piece.GetProperty("source").GetString()));
        Assert.All(outfit.Pieces(), piece => Assert.False(string.IsNullOrEmpty(piece.GetProperty("name").GetString())));
        var narration = outfit.GetProperty("narration");
        Assert.Equal("Template", narration.GetProperty("provider").GetString());
        Assert.Contains("ağmur", narration.GetProperty("text").GetString(), StringComparison.Ordinal);
        AssertWearable(outfit, seeds);

        var alternatives = body.GetProperty("alternatives").EnumerateArray().ToList();
        Assert.Equal(GetDailyEndpoint.Alternatives, alternatives.Count);
        Assert.All(alternatives, alternative =>
        {
            AssertWearable(alternative, seeds);
            Assert.NotNull(alternative.Slot("Outerwear"));
            Assert.True(Difference(outfit, alternative) >= 2);
        });
    }

    [Fact]
    public async Task ShufflingGivesADifferentOutfitAndTheSameSeedGivesTheSameOne()
    {
        var (user, seeds) = await FortyPieceUserAsync();
        using var _ = user;
        await user.Client.SetCityAsync("Istanbul", Ct);

        var first = (await user.Client.DailyAsync("?date=tomorrow&seed=0", Ct)).GetProperty("outfit");
        var second = (await user.Client.DailyAsync("?date=tomorrow&seed=1", Ct)).GetProperty("outfit");
        var third = (await user.Client.DailyAsync("?date=tomorrow&seed=2", Ct)).GetProperty("outfit");
        var again = (await user.Client.DailyAsync("?date=tomorrow&seed=0", Ct)).GetProperty("outfit");

        AssertWearable(second, seeds);
        AssertWearable(third, seeds);
        Assert.True(Difference(first, second) >= 2);
        Assert.True(Difference(second, third) >= 2);

        // A repeated request reuses the stored outfit instead of adding another.
        Assert.Equal(first.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("narration").GetProperty("text").GetString(), again.GetProperty("narration").GetProperty("text").GetString());
    }

    [Fact]
    public async Task AnOccasionShapesTheOutfit()
    {
        var (user, seeds) = await FortyPieceUserAsync();
        using var _ = user;

        var office = (await user.Client.DailyAsync("?occasion=office", Ct)).GetProperty("outfit");
        var sport = (await user.Client.DailyAsync("?occasion=sport", Ct)).GetProperty("outfit");

        Assert.Equal("Office", office.GetProperty("occasion").GetString());
        var officeCore = office.Pieces().Where(piece => piece.GetProperty("slot").GetString() is not "Outerwear")
            .Select(piece => seeds[piece.GetProperty("wardrobeItemId").GetGuid()].Formality).ToList();
        Assert.InRange(officeCore.Average(formality => formality), 2.5, 4.5);

        Assert.Equal("sneaker", sport.Slot("Shoes")!.Value.GetProperty("categorySlug").GetString()!.Split('/')[1]);
    }

    [Fact]
    public async Task DailyRejectsBadParameters()
    {
        using var user = await factory.CreateUserAsync(Ct);

        foreach (var query in new[] { "?date=yesterday", $"?date={Tomorrow.AddDays(30):yyyy-MM-dd}", "?occasion=party", "?seed=-1" })
        {
            using var response = await user.Client.GetAsync($"{Base}/daily{query}", Ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, query);
        }

        using var foreign = await user.Client.GetAsync($"{Base}/daily?personaId={Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("Profile.PersonaNotFound", (await foreign.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var anonymous = factory.CreateAnonymousClient();
        using var unauthorized = await anonymous.GetAsync($"{Base}/daily", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task WhenTheWardrobeCannotDressTheDayOneMarkedCatalogPieceCompletesIt()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertWardrobeAsync(user.Id, [new("tee", "ust-giyim/tisort", ProductColor.White, 2), new("jeans", "alt-giyim/jean", ProductColor.Blue, 2, Fit: ProductFit.Slim)], Ct);
        var shoes = await user.Client.SeedProductAsync("ayakkabi/sneaker", "Black", 2, 1499m, cancellationToken: Ct);

        var outfit = (await user.Client.DailyAsync(cancellationToken: Ct)).GetProperty("outfit");

        Assert.Equal("NeedsOne", outfit.GetProperty("completeness").GetString());
        var gap = Assert.Single(outfit.Pieces(), piece => piece.GetProperty("role").GetString() == "Gap");
        Assert.Equal("Shoes", gap.GetProperty("slot").GetString());
        Assert.Equal("Catalog", gap.GetProperty("source").GetString());
        var offer = gap.GetProperty("offer");
        Assert.Equal($"/api/v1/go/{offer.GetProperty("offerId").GetGuid()}", offer.GetProperty("goPath").GetString());
        Assert.Equal(offer.GetProperty("price").GetDecimal(), outfit.GetProperty("completionPrice").GetDecimal());
        Assert.False(gap.TryGetProperty("url", out _));
        Assert.NotEqual(Guid.Empty, shoes);
    }

    [Fact]
    public async Task AnEmptyWardrobeGetsLabelledShoppingIdeas()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.SeedProductAsync("ust-giyim/tisort", "Black", 2, cancellationToken: Ct);
        await user.Client.SeedProductAsync("alt-giyim/jean", "Black", 2, cancellationToken: Ct);
        await user.Client.SeedProductAsync("ayakkabi/sneaker", "Black", 2, cancellationToken: Ct);

        var body = await user.Client.DailyAsync(cancellationToken: Ct);

        Assert.True(body.GetProperty("wardrobeEmpty").GetBoolean());
        Assert.Equal(GetDailyEndpoint.EmptyWardrobeMessage, body.GetProperty("message").GetString());
        var outfit = body.GetProperty("outfit");
        Assert.Equal("Shopping", outfit.GetProperty("kind").GetString());
        Assert.Equal("CatalogOnly", outfit.GetProperty("completeness").GetString());
        Assert.All(outfit.Pieces(), piece =>
        {
            Assert.Equal("Gap", piece.GetProperty("role").GetString());
            Assert.Equal(JsonValueKind.Object, piece.GetProperty("offer").ValueKind);
        });
        Assert.Equal(outfit.Pieces().Sum(piece => piece.GetProperty("offer").GetProperty("price").GetDecimal()), outfit.GetProperty("completionPrice").GetDecimal());
    }

    // ---- how to wear ----

    [Fact]
    public async Task HowToWearABlazerFillsTheGapsWithTheCheapestOffers()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white shirt", "ust-giyim/gomlek", ProductColor.White, 3),
                new("blue shirt", "ust-giyim/gomlek", ProductColor.Blue, 4, Fit: ProductFit.Slim),
                new("grey knit", "ust-giyim/triko", ProductColor.Grey, 3, ProductPattern.Textured),
                new("navy chinos", "alt-giyim/pantolon", ProductColor.Navy, 4),
                new("grey trousers", "alt-giyim/pantolon", ProductColor.Grey, 3, Fit: ProductFit.Slim),
            ],
            Ct);
        var blazer = await user.Client.SeedProductAsync("ust-giyim/blazer", "Black", 4, 2499m, cancellationToken: Ct);

        // A loafer sold by two shops: the lower price has shipping on top, the higher one ships free and is cheaper in the end.
        var (pricey, _) = await user.Client.CreateSellerAsync("Shp", Ct);
        var (free, _) = await user.Client.CreateSellerAsync("Fre", Ct);
        var brand = Unique("Loa");
        var loafer = await user.Client.SeedProductAsync("ayakkabi/loafer", "Black", 4, 1000m, sellerSlug: pricey, brand: brand, modelCode: "LF-1", shippingCost: 79.9m, freeShippingThreshold: 5000m, cancellationToken: Ct);
        Assert.Equal(loafer, await user.Client.SeedProductAsync("ayakkabi/loafer", "Black", 4, 1049m, sellerSlug: free, brand: brand, modelCode: "LF-1", shippingCost: 0m, cancellationToken: Ct));

        // More shoes, so the "no companion in more than two outfits" rule leaves room for more than two outfits.
        await user.Client.SeedProductAsync("ayakkabi/loafer", "Black", 5, 1899m, cancellationToken: Ct);
        await user.Client.SeedProductAsync("ayakkabi/bot", "Black", 3, 2299m, cancellationToken: Ct);

        var cheapest = await factory.WithDbAsync(db => OutfitSources.CheapestOffersAsync(db, [loafer], Ct));
        var freeOffer = await factory.WithDbAsync(db => db.Offers.Where(offer => offer.ProductId == loafer && offer.Seller.Slug == free).Select(offer => offer.Id).SingleAsync(Ct));
        Assert.Equal((freeOffer, 1049m), cheapest[loafer]);

        var body = await user.Client.HowToWearAsync($"?productId={blazer}&count=5", Ct);

        Assert.Equal(blazer, body.GetProperty("productId").GetGuid());
        var outfits = body.GetProperty("outfits").EnumerateArray().ToList();
        Assert.InRange(outfits.Count, 3, 5);

        foreach (var outfit in outfits)
        {
            Assert.Equal("HowToWear", outfit.GetProperty("kind").GetString());
            var anchor = Assert.Single(outfit.Pieces(), piece => piece.GetProperty("role").GetString() == "Anchor");
            Assert.Equal(blazer, anchor.GetProperty("productId").GetGuid());
            Assert.Equal("Outerwear", anchor.GetProperty("slot").GetString());

            // No shoes at home: every outfit needs exactly one piece, priced with its cheapest offer (effective price).
            Assert.Equal("NeedsOne", outfit.GetProperty("completeness").GetString());
            var gap = Assert.Single(outfit.Pieces(), piece => piece.GetProperty("role").GetString() == "Gap");
            Assert.Equal("Shoes", gap.GetProperty("slot").GetString());
            await AssertCheapestAsync(user.Client, gap);

            var completion = outfit.GetProperty("completionPrice").GetDecimal();
            var anchorPrice = outfit.GetProperty("anchorPrice").GetDecimal();
            Assert.Equal(gap.GetProperty("offer").GetProperty("price").GetDecimal(), completion);
            Assert.Equal(completion + anchorPrice, outfit.GetProperty("totalPrice").GetDecimal());
            Assert.Equal(anchor.GetProperty("offer").GetProperty("price").GetDecimal(), anchorPrice);
        }

        // No companion anchors more than two of the outfits.
        var uses = outfits.SelectMany(outfit => outfit.Pieces().Where(piece => piece.GetProperty("role").GetString() != "Anchor")).GroupBy(piece => piece.Key());
        Assert.All(uses, group => Assert.True(group.Count() <= 2, group.Key));
    }

    [Fact]
    public async Task HowToWearAWardrobeItemUsesTheWardrobeAndAnOwnedProductIsAnchoredAsOwned()
    {
        var (user, seeds) = await FortyPieceUserAsync();
        using var _ = user;
        var shirt = seeds.Single(seed => seed.Value.Name == "white shirt").Key;

        var body = await user.Client.HowToWearAsync($"?wardrobeItemId={shirt}&count=3&seed=1", Ct);
        var outfits = body.GetProperty("outfits").EnumerateArray().ToList();

        Assert.Equal(3, outfits.Count);
        Assert.All(outfits, outfit =>
        {
            AssertWearable(outfit, seeds);
            Assert.True(outfit.Pieces().Count(piece => piece.GetProperty("role").GetString() == "Gap") <= 1);
            Assert.Equal(shirt, outfit.Pieces().Single(piece => piece.GetProperty("role").GetString() == "Anchor").GetProperty("wardrobeItemId").GetGuid());
        });

        // Forty pieces leave the catalog little to add: the wardrobe is preferred, and complete outfits are listed first.
        Assert.Contains(outfits, outfit => outfit.GetProperty("completeness").GetString() == "Complete");
        var order = outfits.Select(outfit => Enum.Parse<OutfitCompleteness>(outfit.GetProperty("completeness").GetString()!)).ToList();
        Assert.Equal(order.Order(), order);

        // A product the user added to the wardrobe is anchored as the wardrobe item.
        var product = await user.Client.SeedProductAsync("ust-giyim/gomlek", "Black", 3, cancellationToken: Ct);
        using (var added = await user.Client.FromProductAsync(product, cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        var owned = await user.Client.HowToWearAsync($"?productId={product}&count=2", Ct);
        Assert.Equal(JsonValueKind.Null, owned.GetProperty("productId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, owned.GetProperty("wardrobeItemId").ValueKind);
    }

    [Fact]
    public async Task HowToWearValidatesTheAnchor()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using (var neither = await user.Client.GetAsync($"{Base}/how-to-wear", Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
            Assert.True((await neither.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("productId", out _));
        }

        using (var tooMany = await user.Client.GetAsync($"{Base}/how-to-wear?productId={Guid.NewGuid()}&count=9", Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        }

        using (var missing = await user.Client.GetAsync($"{Base}/how-to-wear?productId={Guid.NewGuid()}", Ct))
        {
            Assert.Equal("Recommendation.ProductNotFound", (await missing.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using var notMine = await user.Client.GetAsync($"{Base}/how-to-wear?wardrobeItemId={Guid.NewGuid()}", Ct);
        Assert.Equal("Recommendation.WardrobeItemNotFound", (await notMine.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    // ---- actions ----

    [Fact]
    public async Task SaveWearAndDismissFollowTheOutfitsLife()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("tee 2", "ust-giyim/tisort", ProductColor.Grey, 2),
                new("jeans", "alt-giyim/jean", ProductColor.Blue, 2, Fit: ProductFit.Slim),
                new("sneakers", "ayakkabi/sneaker", ProductColor.White, 2),
            ],
            Ct);
        var outfit = (await user.Client.DailyAsync(cancellationToken: Ct)).GetProperty("outfit");
        var id = outfit.GetProperty("id").GetGuid();

        using (var save = await user.Client.PostOutfitAsync(id, "save", cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            var saved = await save.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("Saved", saved.GetProperty("status").GetString());
            Assert.NotEqual(JsonValueKind.Null, saved.GetProperty("savedAt").ValueKind);
        }

        var list = await user.Client.GetJsonAsync($"{Base}/saved", Ct);
        Assert.Equal(id, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // Worn: every wardrobe piece is worn today, through the wardrobe's own wear rows; twice is the same as once.
        foreach (var attempt in new[] { 1, 2 })
        {
            using var worn = await user.Client.PostOutfitAsync(id, "worn", cancellationToken: Ct);
            Assert.True(worn.StatusCode == HttpStatusCode.OK, $"attempt {attempt}");
            Assert.Equal("Worn", (await worn.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        }

        var pieceIds = outfit.Pieces().Select(piece => piece.GetProperty("wardrobeItemId").GetGuid()).ToList();
        var wears = await factory.WithDbAsync(db => db.WardrobeWears.AsNoTracking().Where(wear => pieceIds.Contains(wear.ItemId)).ToListAsync(Ct));
        Assert.Equal(pieceIds.Count, wears.Count);
        Assert.All(wears, wear => Assert.Equal(id, wear.OutfitId));
        foreach (var pieceId in pieceIds)
        {
            Assert.Equal(1, (await user.Client.GetItemAsync(pieceId, Ct)).GetProperty("wearCount").GetInt32());
        }

        // Saved and worn: still listed.
        Assert.Single((await user.Client.GetJsonAsync($"{Base}/saved", Ct)).GetProperty("items").EnumerateArray());

        using (var shuffle = await user.Client.PostOutfitAsync(id, "feedback", new { action = "Shuffle" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, shuffle.StatusCode);
        }

        using (var badReason = await user.Client.PostOutfitAsync(id, "feedback", new { action = "Save", reason = "TooFormal" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badReason.StatusCode);
        }

        using (var dismiss = await user.Client.DeleteAsync($"{Base}/{id}?reason=TooCasual", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);
        }

        var dismissed = await user.Client.GetJsonAsync($"{Base}/{id}", Ct);
        Assert.Equal("Dismissed", dismissed.GetProperty("status").GetString());

        using (var saveAgain = await user.Client.PostOutfitAsync(id, "save", cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, saveAgain.StatusCode);
            Assert.Equal("Recommendation.OutfitDismissed", (await saveAgain.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        Assert.Empty((await user.Client.GetJsonAsync($"{Base}/saved", Ct)).GetProperty("items").EnumerateArray());

        var feedback = await factory.WithDbAsync(db => db.OutfitFeedback.AsNoTracking().Where(row => row.OutfitId == id).OrderBy(row => row.Id).ToListAsync(Ct));
        Assert.Equal(
            [OutfitFeedbackAction.Save, OutfitFeedbackAction.Worn, OutfitFeedbackAction.Worn, OutfitFeedbackAction.Shuffle, OutfitFeedbackAction.Dismiss],
            feedback.Select(row => row.Action));
        Assert.Equal(DismissReason.TooCasual, feedback[^1].Reason);

        // The dismissed combination is not suggested again.
        static string Signature(JsonElement outfit) =>
            string.Join('|', outfit.Pieces().Where(piece => piece.GetProperty("slot").GetString() != "Accessory").Select(piece => piece.Key()).Order(StringComparer.Ordinal));

        var next = (await user.Client.DailyAsync(cancellationToken: Ct)).GetProperty("outfit");
        Assert.NotEqual(id, next.GetProperty("id").GetGuid());
        Assert.NotEqual(Signature(dismissed), Signature(next));
        Assert.All(next.Pieces(), piece => Assert.Contains(piece.GetProperty("wardrobeItemId").GetGuid(), ids.Values));
    }

    [Fact]
    public async Task AnOutfitIsOnlyItsOwnersBusiness()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        await factory.InsertWardrobeAsync(owner.Id, [new("tee", "ust-giyim/tisort", ProductColor.White, 2), new("jeans", "alt-giyim/jean", ProductColor.Blue, 2), new("shoes", "ayakkabi/sneaker", ProductColor.White, 2)], Ct);
        var id = (await owner.Client.DailyAsync(cancellationToken: Ct)).GetProperty("outfit").GetProperty("id").GetGuid();

        foreach (var action in new[] { "save", "worn" })
        {
            using var response = await stranger.Client.PostOutfitAsync(id, action, cancellationToken: Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Recommendation.OutfitNotFound", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using var get = await stranger.Client.GetAsync($"{Base}/{id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var delete = await stranger.Client.DeleteAsync($"{Base}/{id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task ACatalogOnlyOutfitCannotBeWorn()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.SeedProductAsync("ust-giyim/tisort", "Black", 2, cancellationToken: Ct);
        await user.Client.SeedProductAsync("alt-giyim/jean", "Black", 2, cancellationToken: Ct);
        await user.Client.SeedProductAsync("ayakkabi/sneaker", "Black", 2, cancellationToken: Ct);
        var id = (await user.Client.DailyAsync(cancellationToken: Ct)).GetProperty("outfit").GetProperty("id").GetGuid();

        using var worn = await user.Client.PostOutfitAsync(id, "worn", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Conflict, worn.StatusCode);
        Assert.Equal("Recommendation.NothingToWear", (await worn.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    // ---- wardrobe numbers ----

    [Fact]
    public async Task PossibleOutfitsCountsWearableCombinationsAndFollowsTheWardrobe()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("white tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("black tee", "ust-giyim/tisort", ProductColor.Black, 2),
                new("jeans", "alt-giyim/jean", ProductColor.Blue, 2),
                new("sneakers", "ayakkabi/sneaker", ProductColor.White, 2),
                new("suit trousers", "alt-giyim/pantolon", ProductColor.Grey, 5),
            ],
            Ct);

        // Two tees with the jeans; the suit trousers are too formal for both.
        Assert.Equal(2, (await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/stats", Ct)).GetProperty("possibleOutfits").GetInt32());

        await factory.InsertWardrobeAsync(user.Id, [new("chinos", "alt-giyim/pantolon", ProductColor.Beige, 2)], Ct);
        Assert.Equal(4, (await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/stats", Ct)).GetProperty("possibleOutfits").GetInt32());
    }

    [Fact]
    public async Task AGapsUnlocksEstimateIsCountedByTheEngine()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertWardrobeAsync(
            user.Id,
            [
                new("tee", "ust-giyim/tisort", ProductColor.White, 2),
                new("jeans", "alt-giyim/jean", ProductColor.Navy, 2),
                new("sneakers", "ayakkabi/sneaker", ProductColor.Black, 2),
                new("chinos", "alt-giyim/pantolon", ProductColor.Beige, 3),
                new("loafers", "ayakkabi/loafer", ProductColor.Brown, 4),
            ],
            Ct);

        var gaps = (await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/gaps", Ct)).GetProperty("gaps").EnumerateArray().ToList();
        var top = gaps.Single(gap => gap.GetProperty("kind").GetString() == "missing_smart_top");

        // The rough estimate would say 2 bottoms × 2 shoes = 4; the engine knows a smart top with jeans and formal loafers spans
        // three formality steps, so that one is not an outfit.
        Assert.Equal(3, top.GetProperty("unlocksEstimate").GetInt32());
        Assert.Equal(gaps.Max(gap => gap.GetProperty("unlocksEstimate").GetInt32()), gaps[0].GetProperty("unlocksEstimate").GetInt32());
    }

    [Fact]
    public async Task TheCityIsPartOfTheProfile()
    {
        using var user = await factory.CreateUserAsync(Ct);

        await user.Client.SetCityAsync("  Istanbul ", Ct);
        Assert.Equal("Istanbul", (await user.Client.GetJsonAsync("/api/v1/me/profile", Ct)).GetProperty("city").GetString());

        using var invalid = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { city = "Ist<script>" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var tooLong = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { city = new string('a', 81) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        await user.Client.SetCityAsync(null, Ct);
        Assert.Equal(JsonValueKind.Null, (await user.Client.GetJsonAsync("/api/v1/me/profile", Ct)).GetProperty("city").ValueKind);
    }

    /// <summary>The gap's offer is the product's first offer on the product page, which is ordered by effective price.</summary>
    private static async Task AssertCheapestAsync(HttpClient client, JsonElement gap)
    {
        var product = await client.GetJsonAsync($"/api/v1/catalog/products/{gap.GetProperty("productId").GetGuid()}", Ct);
        var best = product.GetProperty("offers").EnumerateArray().First(offer => offer.GetProperty("stockStatus").GetString() is "InStock" or "LowStock");
        var offer = gap.GetProperty("offer");

        Assert.Equal(best.GetProperty("id").GetGuid(), offer.GetProperty("offerId").GetGuid());
        Assert.Equal(best.GetProperty("effectivePrice").GetDecimal(), offer.GetProperty("price").GetDecimal());
        Assert.Equal(best.GetProperty("seller").GetProperty("slug").GetString(), offer.GetProperty("seller").GetProperty("slug").GetString());
    }
}
