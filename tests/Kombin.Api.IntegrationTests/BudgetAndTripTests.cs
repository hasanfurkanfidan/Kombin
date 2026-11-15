using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.RecommendationClientExtensions;
using static Kombin.Api.IntegrationTests.TripClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Budget outfits and trip packing end to end. The catalog is shared by every test (and full of cheap products), so budget tests assert what must
/// hold for whatever the engine picks, checked against the database, rather than which product it picks.
/// </summary>
public sealed class BudgetAndTripTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TestUser> FortyPieceUserAsync(string? city = "Istanbul")
    {
        var user = await factory.CreateUserAsync(Ct);
        await factory.InsertWardrobeAsync(user.Id, FortyPieces, Ct);
        if (city is not null)
        {
            await user.Client.SetCityAsync(city, Ct);
        }

        return user;
    }

    private static readonly string[] WomenOrUnisex = ["Women", "Unisex"];

    private static List<JsonElement> Outfits(JsonElement body) => [.. body.GetProperty("outfits").EnumerateArray()];

    private static IEnumerable<JsonElement> CatalogPieces(JsonElement outfit) => outfit.Pieces().Where(piece => piece.GetProperty("source").GetString() == "Catalog");

    /// <summary>What every budget outfit must be: within the budget (unless flagged), at least one piece bought, buy links through /go?src=budget, owned pieces free.</summary>
    private static void AssertBudgetOutfit(JsonElement outfit, decimal budget)
    {
        var total = outfit.GetProperty("totalPrice").GetDecimal();
        var bought = CatalogPieces(outfit).ToList();

        Assert.NotEmpty(bought);
        Assert.Equal(bought.Sum(piece => piece.GetProperty("price").GetDecimal()), total);
        Assert.InRange(outfit.GetProperty("score").GetInt32(), OutfitScoring.AcceptanceThreshold, 100);
        Assert.Equal(outfit.Pieces().Count(piece => piece.GetProperty("source").GetString() == "Wardrobe"), outfit.GetProperty("ownedCount").GetInt32());

        if (outfit.TryGetProperty("overBudgetBy", out var over))
        {
            Assert.Equal(total - budget, over.GetDecimal());
            Assert.Equal(0, outfit.GetProperty("remainingBudget").GetDecimal());
        }
        else
        {
            Assert.True(total <= budget, $"{total} > {budget}");
            Assert.Equal(budget - total, outfit.GetProperty("remainingBudget").GetDecimal());
        }

        foreach (var piece in bought)
        {
            var offer = piece.GetProperty("offer");
            var offerId = offer.GetProperty("offerId").GetGuid();
            Assert.Equal($"/api/v1/go/{offerId}?src=budget", offer.GetProperty("goPath").GetString());
            Assert.Equal(piece.GetProperty("price").GetDecimal(), offer.GetProperty("price").GetDecimal());
            Assert.DoesNotContain("shop.example.com", piece.GetRawText(), StringComparison.Ordinal);
        }

        Assert.All(
            outfit.Pieces().Where(piece => piece.GetProperty("source").GetString() == "Wardrobe"),
            piece =>
            {
                Assert.Equal(0, piece.GetProperty("price").GetDecimal());
                Assert.Equal(JsonValueKind.Null, piece.GetProperty("offer").ValueKind);
            });
    }

    // ---- budget, signed in ----

    [Fact]
    public async Task ASignedInBudgetUsesTheWardrobeFirstStaysWithinTheBudgetAndIsStored()
    {
        using var user = await FortyPieceUserAsync();
        await user.Client.SeedProductAsync("ayakkabi/loafer", "Black", 3, 450m, cancellationToken: Ct);
        await user.Client.SeedProductAsync("ust-giyim/gomlek", "White", 3, 380m, cancellationToken: Ct);

        var body = await user.Client.BudgetOkAsync(new { budget = 1500, occasion = "office" }, Ct);

        Assert.Equal(1500, body.GetProperty("budget").GetDecimal());
        Assert.True(body.GetProperty("useWardrobe").GetBoolean());
        Assert.False(body.GetProperty("overBudget").GetBoolean());
        Assert.Equal("Istanbul", body.GetProperty("weather").GetProperty("city").GetString());
        var outfits = Outfits(body).ToList();
        Assert.InRange(outfits.Count, 1, 3);
        Assert.All(outfits, outfit => AssertBudgetOutfit(outfit, 1500));

        // A forty-piece wardrobe: the best budget outfits buy one or two pieces and keep the rest.
        Assert.Contains(outfits, outfit => outfit.GetProperty("ownedCount").GetInt32() >= 2);

        // Stored as a Budget outfit: the outfit endpoints work on it and keep the budget's click source.
        var id = outfits[0].GetProperty("id").GetGuid();
        Assert.Equal("Suggested", outfits[0].GetProperty("status").GetString());
        var stored = await user.Client.GetJsonAsync($"{Base}/{id}", Ct);
        Assert.Equal("Budget", stored.GetProperty("kind").GetString());
        Assert.All(
            stored.Pieces().Where(piece => piece.GetProperty("source").GetString() == "Catalog"),
            piece => Assert.EndsWith("?src=budget", piece.GetProperty("offer").GetProperty("goPath").GetString(), StringComparison.Ordinal));
        using (var save = await user.Client.PostOutfitAsync(id, "save", cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        // Diversity: no two outfits share more than half of their bought pieces.
        foreach (var left in outfits)
        {
            foreach (var right in outfits.Where(other => other.GetProperty("id").GetGuid() != left.GetProperty("id").GetGuid()))
            {
                var leftBought = CatalogPieces(left).Select(piece => piece.GetProperty("productId").GetGuid()).ToList();
                var rightBought = CatalogPieces(right).Select(piece => piece.GetProperty("productId").GetGuid()).ToList();
                Assert.True(2 * leftBought.Intersect(rightBought).Count() <= Math.Min(leftBought.Count, rightBought.Count));
            }
        }

        // The next seed gives other outfits, still within the budget. (That equal inputs give equal outfits is BudgetSearchTests' job: here other
        // tests add products to the shared catalog between two requests.)
        var next = await user.Client.BudgetOkAsync(new { budget = 1500, occasion = "office", seed = 1 }, Ct);
        Assert.All(Outfits(next), outfit => AssertBudgetOutfit(outfit, 1500));
    }

    [Fact]
    public async Task WithoutTheWardrobeEveryPieceIsBoughtAndTheLineNeverClaimsTheRestIsOwned()
    {
        using var user = await FortyPieceUserAsync(city: null);
        await user.Client.SeedCapsuleAsync(2, 250m, cancellationToken: Ct);
        await user.Client.SeedCapsuleAsync(2, 400m, cancellationToken: Ct);

        var body = await user.Client.BudgetOkAsync(new { budget = 3000, useWardrobe = false, count = 5 }, Ct);

        Assert.False(body.GetProperty("useWardrobe").GetBoolean());
        Assert.NotEmpty(Outfits(body));
        Assert.All(Outfits(body), outfit =>
        {
            AssertBudgetOutfit(outfit, 3000);
            Assert.Equal(0, outfit.GetProperty("ownedCount").GetInt32());
            Assert.Equal("CatalogOnly", outfit.GetProperty("completeness").GetString());
            Assert.NotEqual("Gap", outfit.GetProperty("rule").GetString());
        });
    }

    [Fact]
    public async Task SizesComeFromTheSizeMapAndAreUnknownOtherwise()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.SeedCapsuleAsync(2, 150m, cancellationToken: Ct);
        var request = new { budget = 600, occasion = "weekend", useWardrobe = false, count = 5 };

        // No size known: every bought piece says so.
        var first = await user.Client.BudgetOkAsync(request, Ct);
        var pieces = Outfits(first).SelectMany(CatalogPieces).ToList();
        Assert.NotEmpty(pieces);
        Assert.All(pieces, piece =>
        {
            Assert.True(piece.GetProperty("sizeUnknown").GetBoolean());
            Assert.Equal(JsonValueKind.Null, piece.GetProperty("size").ValueKind);
        });

        // The caller wears M at the brands of those pieces.
        var productIds = pieces.Select(piece => piece.GetProperty("productId").GetGuid()).Distinct().ToList();
        await factory.WithDbAsync(async db =>
        {
            var products = await db.Products.Where(product => productIds.Contains(product.Id)).Select(product => new { product.BrandId, Slug = product.Category.Slug }).ToListAsync(Ct);
            foreach (var key in products.Select(product => (product.BrandId, Group: SizeGroups.ForCategorySlug(product.Slug))).Distinct())
            {
                db.SizeEntries.Add(new SizeEntry { UserId = user.Id, BrandId = key.BrandId, Group = key.Group, Size = "M", Source = SizeSource.UserEntered });
            }

            await db.SaveChangesAsync(Ct);
            return 0;
        });

        var second = await user.Client.BudgetOkAsync(request, Ct);
        var known = 0;

        foreach (var piece in Outfits(second).SelectMany(CatalogPieces))
        {
            var productId = piece.GetProperty("productId").GetGuid();
            var offerId = piece.GetProperty("offer").GetProperty("offerId").GetGuid();
            var (brandId, slug, sizes) = await factory.WithDbAsync(async db =>
            {
                var product = await db.Products.Where(candidate => candidate.Id == productId).Select(candidate => new { candidate.BrandId, candidate.Category.Slug }).SingleAsync(Ct);
                var offerSizes = await db.Offers.Where(offer => offer.Id == offerId).Select(offer => offer.AvailableSizes).SingleAsync(Ct);
                return (product.BrandId, product.Slug, offerSizes);
            });
            var entries = await factory.WithDbAsync(db => db.SizeEntries.CountAsync(entry => entry.UserId == user.Id && entry.Group == SizeGroups.ForCategorySlug(slug), Ct));
            var own = await factory.WithDbAsync(db => db.SizeEntries.AnyAsync(entry => entry.UserId == user.Id && entry.BrandId == brandId, Ct));

            // Known: the caller's own entry for the brand, or two other brands agreeing on M (inferred).
            if (own || entries >= 2)
            {
                if (piece.GetProperty("sizeUnknown").GetBoolean())
                {
                    Assert.Empty(sizes);
                }
                else
                {
                    Assert.Equal("M", piece.GetProperty("size").GetString());
                    Assert.Contains("M", sizes);
                    known++;
                }
            }
            else
            {
                Assert.True(piece.GetProperty("sizeUnknown").GetBoolean());
            }
        }

        Assert.True(known > 0, "no piece came back in the caller's size");
    }

    [Fact]
    public async Task WhenNothingFitsTheClosestOutfitComesBackOnceFlaggedOverBudget()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { audience = "Men" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        }

        // An event outfit for men: nothing in the catalog dresses it for 100 ₺, not even without shoes (the shirt and trousers alone are
        // 120 ₺); with the shoes these three come to 150 ₺, the most the over-budget answer may cost.
        await user.Client.SeedProductAsync("ust-giyim/gomlek", "White", 5, 60m, audience: "Men", cancellationToken: Ct);
        await user.Client.SeedProductAsync("alt-giyim/pantolon", "Black", 5, 60m, audience: "Men", cancellationToken: Ct);
        await user.Client.SeedProductAsync("ayakkabi/loafer", "Black", 5, 30m, audience: "Men", cancellationToken: Ct);

        var body = await user.Client.BudgetOkAsync(new { budget = 100, occasion = "event", useWardrobe = false }, Ct);

        Assert.True(body.GetProperty("overBudget").GetBoolean());
        Assert.False(body.GetProperty("withoutShoes").GetBoolean());
        var outfit = Assert.Single(Outfits(body));
        AssertBudgetOutfit(outfit, 100);
        var total = outfit.GetProperty("totalPrice").GetDecimal();
        Assert.InRange(total, 100.01m, 150m);
        Assert.Equal(total - 100, outfit.GetProperty("overBudgetBy").GetDecimal());
        Assert.StartsWith("Bu bütçeyle tam bir kombin çıkmadı; en yakını ", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A men's office outfit (band 3-4): a 100 ₺ top and a 120 ₺ bottom, shoes at 2,500 ₺. Men and the office band keep the shared catalog's cheap
    /// (women's, casual) products out, so 300 ₺ cannot buy any shoes.
    /// </summary>
    private static async Task SeedShoelessCapsuleAsync(HttpClient client)
    {
        await client.SeedProductAsync("ust-giyim/gomlek", "White", 4, 100m, audience: "Men", cancellationToken: Ct);
        await client.SeedProductAsync("alt-giyim/pantolon", "Navy", 4, 120m, audience: "Men", cancellationToken: Ct);
        await client.SeedProductAsync("ayakkabi/loafer", "Black", 4, 2500m, audience: "Men", cancellationToken: Ct);
    }

    [Fact]
    public async Task ALowBudgetThatCannotPayForShoesGetsOutfitsWithoutShoes()
    {
        using var anonymous = factory.CreateAnonymousClient();
        await SeedShoelessCapsuleAsync(anonymous);

        var body = await anonymous.BudgetOkAsync(new { budget = 300, occasion = "office", audience = "Men" }, Ct, anonymous: true);

        Assert.True(body.GetProperty("withoutShoes").GetBoolean());
        Assert.False(body.GetProperty("overBudget").GetBoolean());
        Assert.Equal(BudgetOutfits.WithoutShoesMessage, body.GetProperty("message").GetString());
        Assert.NotEmpty(Outfits(body));
        Assert.All(Outfits(body), outfit =>
        {
            AssertBudgetOutfit(outfit, 300);
            Assert.True(outfit.GetProperty("withoutShoes").GetBoolean());
            Assert.Null(outfit.Slot("Shoes"));
            Assert.NotNull(outfit.Slot("Top"));
            Assert.NotNull(outfit.Slot("Bottom"));
        });

        // With room for the shoes, the outfits have them again and say nothing about it.
        var roomy = await anonymous.BudgetOkAsync(new { budget = 3000, occasion = "office", audience = "Men" }, Ct, anonymous: true);
        Assert.False(roomy.GetProperty("withoutShoes").GetBoolean());
        Assert.Equal(JsonValueKind.Null, roomy.GetProperty("message").ValueKind);
        Assert.All(Outfits(roomy), outfit => Assert.NotNull(outfit.Slot("Shoes")));
    }

    [Fact]
    public async Task IncludeShoesFalseLeavesThemOutWhateverTheBudget()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await SeedShoelessCapsuleAsync(user.Client);
        await factory.InsertWardrobeAsync(user.Id, [new WardrobeSeed("own loafers", "ayakkabi/loafer", Persistence.Entities.Catalog.ProductColor.Brown, 4)], Ct);

        var anonymous = await user.Client.BudgetOkAsync(new { budget = 3000, occasion = "office", audience = "Men", includeShoes = false }, Ct, anonymous: true);
        var signedIn = await user.Client.BudgetOkAsync(new { budget = 3000, occasion = "office", includeShoes = false }, Ct);

        foreach (var body in new[] { anonymous, signedIn })
        {
            Assert.True(body.GetProperty("withoutShoes").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("message").ValueKind);
            Assert.NotEmpty(Outfits(body));
            Assert.All(Outfits(body), outfit =>
            {
                AssertBudgetOutfit(outfit, 3000);
                Assert.Null(outfit.Slot("Shoes"));
            });
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostJsonAsync(Budget, new { budget = 500, includeShoes = "maybe" }, Ct)).Status);
    }

    [Fact]
    public async Task ASignedInUserWithShoesKeepsWearingThemOnALowBudget()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { audience = "Men" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        }

        await SeedShoelessCapsuleAsync(user.Client);
        var shoes = await factory.InsertWardrobeAsync(user.Id, [new WardrobeSeed("own loafers", "ayakkabi/loafer", Persistence.Entities.Catalog.ProductColor.Brown, 4)], Ct);

        var body = await user.Client.BudgetOkAsync(new { budget = 300, occasion = "office" }, Ct);

        Assert.False(body.GetProperty("withoutShoes").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("message").ValueKind);
        Assert.NotEmpty(Outfits(body));
        Assert.All(Outfits(body), outfit =>
        {
            AssertBudgetOutfit(outfit, 300);
            Assert.False(outfit.GetProperty("withoutShoes").GetBoolean());
            Assert.Equal(shoes["own loafers"], outfit.Slot("Shoes")!.Value.GetProperty("wardrobeItemId").GetGuid());
        });
    }

    [Fact]
    public async Task ABudgetNeedsAValidAmountAndSomeoneElsesPersonaIsNotFound()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var persona = await other.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        foreach (var request in new object[] { new { budget = 50 }, new { budget = 200_000 }, new { }, new { budget = 500, count = 9 }, new { budget = 500, occasion = "beach" } })
        {
            var (status, _) = await user.Client.PostJsonAsync(Budget, request, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, status);
        }

        var (notFound, problem) = await user.Client.PostJsonAsync(Budget, new { budget = 500, personaId = persona }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, notFound);
        Assert.Equal("Profile.PersonaNotFound", problem.GetProperty("errorCode").GetString());

        using var anonymous = factory.CreateAnonymousClient();
        var (unauthorized, _) = await anonymous.PostJsonAsync(Budget, new { budget = 500 }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized);
    }

    // ---- budget, anonymous ----

    [Fact]
    public async Task AnAnonymousBudgetNeedsAnAudienceAndAnswersWithCatalogPiecesOnly()
    {
        using var anonymous = factory.CreateAnonymousClient();
        await anonymous.SeedCapsuleAsync(2, 180m, cancellationToken: Ct);

        var (missing, problem) = await anonymous.PostJsonAsync(AnonymousBudget, new { budget = 800 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.True(problem.GetProperty("errors").TryGetProperty("audience", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostJsonAsync(AnonymousBudget, new { budget = 800, audience = "Kids" }, Ct)).Status);

        var body = await anonymous.BudgetOkAsync(new { budget = 800, audience = "Women", occasion = "weekend" }, Ct, anonymous: true);

        Assert.Equal("Women", body.GetProperty("audience").GetString());
        Assert.False(body.GetProperty("useWardrobe").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("weather").ValueKind);
        Assert.NotEmpty(Outfits(body));
        Assert.All(Outfits(body), outfit =>
        {
            AssertBudgetOutfit(outfit, 800);
            Assert.Equal(JsonValueKind.Null, outfit.GetProperty("id").ValueKind);
            Assert.Equal(0, outfit.GetProperty("ownedCount").GetInt32());
            Assert.All(CatalogPieces(outfit), piece => Assert.True(piece.GetProperty("sizeUnknown").GetBoolean()));
            Assert.False(string.IsNullOrWhiteSpace(outfit.GetProperty("narration").GetProperty("text").GetString()));
        });

        // Women see women's and unisex products only.
        var productIds = Outfits(body).SelectMany(CatalogPieces).Select(piece => piece.GetProperty("productId").GetGuid()).Distinct().ToList();
        var audiences = await factory.WithDbAsync(db => db.Products.Where(product => productIds.Contains(product.Id)).Select(product => product.Audience.ToString()).Distinct().ToListAsync(Ct));
        Assert.All(audiences, audience => Assert.Contains(audience, WomenOrUnisex));
    }

    [Fact]
    public async Task ASignedInCallerOnTheAnonymousRouteGetsTheAnonymousAnswer()
    {
        using var user = await FortyPieceUserAsync();

        var body = await user.Client.BudgetOkAsync(new { budget = 1500, audience = "Unisex" }, Ct, anonymous: true);

        Assert.All(Outfits(body), outfit => Assert.Equal(0, outfit.GetProperty("ownedCount").GetInt32()));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Outfits.CountAsync(outfit => outfit.UserId == user.Id && outfit.Kind == OutfitKind.Budget, Ct)));
    }

    // ---- trips ----

    [Fact]
    public async Task AFourDayTripForAFortyPieceWardrobeDressesEveryDayWithFewPieces()
    {
        using var user = await FortyPieceUserAsync();

        var plan = await user.Client.PlanTripOkAsync(TripRequest("Istanbul", days: 4, occasions: new[] { new { date = FirstDay.AddDays(1), occasion = "office" } }), Ct);

        Assert.Equal("Istanbul", plan.GetProperty("city").GetString());
        var coverage = plan.GetProperty("coverage");
        Assert.Equal(4, coverage.GetProperty("covered").GetInt32());
        Assert.Equal(100, coverage.GetProperty("percent").GetInt32());
        Assert.Empty(plan.GetProperty("gaps").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("message").ValueKind);

        var days = plan.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(4, days.Count);
        Assert.All(days, day => Assert.Equal("Forecast", day.GetProperty("weather").GetProperty("source").GetString()));
        Assert.Equal("Office", days[1].GetProperty("occasion").GetString());

        // The packing list is exactly the pieces the days wear, each with its number of days.
        var packed = plan.GetProperty("pieces").EnumerateArray().SelectMany(group => group.GetProperty("items").EnumerateArray()).ToList();
        var worn = days.SelectMany(day => day.GetProperty("outfit").GetProperty("pieces").EnumerateArray()).GroupBy(piece => piece.GetProperty("wardrobeItemId").GetGuid()).ToList();
        Assert.Equal(plan.GetProperty("pieceCount").GetInt32(), packed.Count);
        Assert.Equal(worn.Count, packed.Count);
        Assert.All(packed, item => Assert.Equal(worn.Single(group => group.Key == item.GetProperty("wardrobeItemId").GetGuid()).Count(), item.GetProperty("days").GetInt32()));
        Assert.InRange(packed.Count, 4, 9);

        // Wear rules: a top never two days running and at most twice; no outfit two days running.
        var tops = days.Select(day => day.GetProperty("outfit").GetProperty("pieces").EnumerateArray().Single(piece => piece.GetProperty("slot").GetString() == "Top").GetProperty("wardrobeItemId").GetGuid()).ToList();
        for (var index = 1; index < tops.Count; index++)
        {
            Assert.NotEqual(tops[index - 1], tops[index]);
        }

        Assert.All(tops.GroupBy(top => top), group => Assert.True(group.Count() <= 2));
        Assert.All(days, day => Assert.False(string.IsNullOrWhiteSpace(day.GetProperty("outfit").GetProperty("narration").GetProperty("text").GetString())));
    }

    [Fact]
    public async Task EachDaySaysWhereItsWeatherCameFrom()
    {
        using var user = await FortyPieceUserAsync();

        var far = await user.Client.PlanTripOkAsync(TripRequest("Istanbul", days: 2, offsetDays: 40), Ct);
        Assert.All(far.GetProperty("days").EnumerateArray(), day => Assert.Equal("Normal", day.GetProperty("weather").GetProperty("source").GetString()));

        var unknown = await user.Client.PlanTripOkAsync(TripRequest("Nowhere", days: 2), Ct);
        Assert.All(unknown.GetProperty("days").EnumerateArray(), day =>
        {
            var weather = day.GetProperty("weather");
            Assert.Equal("Season", weather.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, weather.GetProperty("tempMaxC").ValueKind);
        });

        // A cold destination asks for a layer every day.
        var cold = await user.Client.PlanTripOkAsync(TripRequest("Coldville", days: 3), Ct);
        Assert.All(cold.GetProperty("days").EnumerateArray(), day =>
            Assert.Contains(day.GetProperty("outfit").GetProperty("pieces").EnumerateArray(), piece => piece.GetProperty("slot").GetString() == "Outerwear"));
    }

    [Fact]
    public async Task AThinWardrobeGetsAnHonestAnswerWithTheGapsAndNoInventedPieces()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.SeedCapsuleAsync(2, 350m, cancellationToken: Ct);
        await factory.InsertWardrobeAsync(user.Id, FortyPieces.Where(seed => seed.CategorySlug.StartsWith("ust-giyim/tisort", StringComparison.Ordinal)).Take(2), Ct);

        var plan = await user.Client.PlanTripOkAsync(TripRequest("Istanbul", days: 3), Ct);

        Assert.Equal(0, plan.GetProperty("coverage").GetProperty("covered").GetInt32());
        Assert.Equal(0, plan.GetProperty("pieceCount").GetInt32());
        Assert.Equal(TripPlanning.ThinWardrobeMessage, plan.GetProperty("message").GetString());
        Assert.All(plan.GetProperty("days").EnumerateArray(), day => Assert.Equal(JsonValueKind.Null, day.GetProperty("outfit").ValueKind));

        var gaps = plan.GetProperty("gaps").EnumerateArray().ToList();
        Assert.Contains(gaps, gap => gap.GetProperty("slot").GetString() == "Bottom" && gap.GetProperty("reason").GetString() == "Missing");
        Assert.Contains(gaps, gap => gap.GetProperty("slot").GetString() == "Shoes" && gap.GetProperty("reason").GetString() == "Missing");
        Assert.All(gaps, gap => Assert.Equal(3, gap.GetProperty("days").GetArrayLength()));
        Assert.Contains(gaps, gap => gap.GetProperty("suggestion").ValueKind == JsonValueKind.Object);
        Assert.All(gaps.Where(gap => gap.GetProperty("suggestion").ValueKind == JsonValueKind.Object), gap =>
        {
            var offer = gap.GetProperty("suggestion").GetProperty("offer");
            Assert.Equal($"/api/v1/go/{offer.GetProperty("offerId").GetGuid()}?src=trip", offer.GetProperty("goPath").GetString());
        });
    }

    [Fact]
    public async Task ATripPlanIsValidated()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        object[] invalid =
        [
            TripRequest("Istanbul", days: 15),
            TripRequest("", days: 2),
            new { city = "Istanbul", startDate = today.AddDays(-3), endDate = today.AddDays(1) },
            new { city = "Istanbul", startDate = today.AddDays(5), endDate = today.AddDays(3) },
            new { city = "Istanbul", startDate = today.AddDays(400), endDate = today.AddDays(401) },
            TripRequest("Istanbul", days: 2, maxPieces: 1),
            TripRequest("Istanbul", days: 2, occasions: new[] { new { date = today.AddDays(30), occasion = "office" } }),
            TripRequest("Istanbul", days: 2, occasions: new[] { new { date = FirstDay, occasion = "beach" } }),
        ];

        foreach (var request in invalid)
        {
            var (status, body) = await user.Client.PostJsonAsync($"{Trips}/plan", request, Ct);
            Assert.True(status == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(request)}: {(int)status} {body}");
        }
    }

    [Fact]
    public async Task SavedTripsBelongToTheirOwnerAndCanBeReplannedAndDeleted()
    {
        using var user = await FortyPieceUserAsync();
        using var other = await factory.CreateUserAsync(Ct);

        var (created, trip) = await user.Client.SaveTripAsync("İzmir hafta sonu", TripRequest("Istanbul", days: 3, laundry: true), Ct);
        Assert.Equal(HttpStatusCode.Created, created);
        var id = trip.GetProperty("id").GetGuid();
        Assert.Equal("İzmir hafta sonu", trip.GetProperty("name").GetString());
        Assert.Equal("Istanbul", trip.GetProperty("request").GetProperty("city").GetString());
        Assert.True(trip.GetProperty("request").GetProperty("laundry").GetBoolean());
        Assert.Equal(3, trip.GetProperty("plan").GetProperty("coverage").GetProperty("covered").GetInt32());

        var list = await user.Client.GetJsonAsync(Trips, Ct);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(trip.GetProperty("plan").GetProperty("pieceCount").GetInt32(), item.GetProperty("pieceCount").GetInt32());
        Assert.Equal(100, item.GetProperty("coverage").GetProperty("percent").GetInt32());

        var read = await user.Client.GetJsonAsync($"{Trips}/{id}", Ct);
        Assert.Equal(trip.GetProperty("plan").GetRawText(), read.GetProperty("plan").GetRawText());

        // Someone else sees nothing, changes nothing.
        Assert.Empty((await other.Client.GetJsonAsync(Trips, Ct)).GetProperty("items").EnumerateArray());
        using (var peek = await other.Client.GetAsync($"{Trips}/{id}", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        }

        using (var steal = await other.Client.DeleteAsync($"{Trips}/{id}", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        }

        // Re-planning stores a new plan; a removed piece is shown as missing until then.
        var plannedAt = trip.GetProperty("plannedAt").GetDateTimeOffset();
        var removed = read.GetProperty("plan").GetProperty("days")[0].GetProperty("outfit").GetProperty("pieces")[0].GetProperty("wardrobeItemId").GetGuid();
        await factory.WithDbAsync(db => db.WardrobeItems.Where(piece => piece.Id == removed).ExecuteUpdateAsync(set => set.SetProperty(piece => piece.IsArchived, true), Ct));
        var stale = await user.Client.GetJsonAsync($"{Trips}/{id}", Ct);
        Assert.Contains(
            stale.GetProperty("plan").GetProperty("pieces").EnumerateArray().SelectMany(group => group.GetProperty("items").EnumerateArray()),
            piece => piece.GetProperty("wardrobeItemId").GetGuid() == removed && piece.GetProperty("missing").GetBoolean());

        var (replanned, fresh) = await user.Client.PostJsonAsync($"{Trips}/{id}/replan", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, replanned);
        Assert.True(fresh.GetProperty("plannedAt").GetDateTimeOffset() >= plannedAt);
        Assert.DoesNotContain(
            fresh.GetProperty("plan").GetProperty("pieces").EnumerateArray().SelectMany(group => group.GetProperty("items").EnumerateArray()),
            piece => piece.GetProperty("wardrobeItemId").GetGuid() == removed);

        using (var delete = await user.Client.DeleteAsync($"{Trips}/{id}", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using (var gone = await user.Client.GetAsync($"{Trips}/{id}", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
    }

    [Fact]
    public async Task AFinishedTripCannotBeReplannedAndFiftyTripsAreTheLimit()
    {
        using var user = await FortyPieceUserAsync();

        var over = await factory.WithDbAsync(async db =>
        {
            var trips = Enumerable.Range(0, TripPlanning.MaxSavedTrips).Select(index => new Trip
            {
                UserId = user.Id,
                Name = string.Create(CultureInfo.InvariantCulture, $"Eski {index}"),
                City = "Istanbul",
                StartDate = new DateOnly(2025, 1, 1),
                EndDate = new DateOnly(2025, 1, 3),
                RequestJson = """{"city":"Istanbul","startDate":"2025-01-01","endDate":"2025-01-03"}""",
                PlanJson = """{"city":"Istanbul","startDate":"2025-01-01","endDate":"2025-01-03","days":[],"gaps":[],"message":null}""",
                PlannedAt = DateTimeOffset.UtcNow,
            }).ToList();
            db.Trips.AddRange(trips);
            await db.SaveChangesAsync(Ct);
            return trips[0].Id;
        });

        var (finished, problem) = await user.Client.PostJsonAsync($"{Trips}/{over}/replan", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, finished);
        Assert.Equal("Recommendation.TripOver", problem.GetProperty("errorCode").GetString());

        var (full, limit) = await user.Client.SaveTripAsync("Bir tane daha", TripRequest("Istanbul", days: 2), Ct);
        Assert.Equal(HttpStatusCode.Conflict, full);
        Assert.Equal("Recommendation.TooManyTrips", limit.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task TheNewClickSourcesAreRecorded()
    {
        using var anonymous = factory.CreateAnonymousClient();
        await anonymous.SeedCapsuleAsync(2, 220m, cancellationToken: Ct);
        var body = await anonymous.BudgetOkAsync(new { budget = 2000, audience = "Unisex" }, Ct, anonymous: true);
        var goPath = Outfits(body).SelectMany(CatalogPieces).First().GetProperty("offer").GetProperty("goPath").GetString()!;

        using var redirect = await anonymous.GetAsync(goPath, Ct);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);

        var offerId = Guid.Parse(goPath["/api/v1/go/".Length..goPath.IndexOf('?', StringComparison.Ordinal)]);
        var sources = await factory.WithDbAsync(db => db.Clicks.Where(click => click.OfferId == offerId).Select(click => click.Source).ToListAsync(Ct));
        Assert.Contains("budget", sources);
        Assert.Equal("trip", Features.Engagement.ClickSources.Normalize("trip"));
    }
}
