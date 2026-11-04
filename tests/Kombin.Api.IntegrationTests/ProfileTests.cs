using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;
using static Kombin.Api.IntegrationTests.ProfileClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class ProfileTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!);

    [Fact]
    public async Task EveryEndpointNeedsASignedInUser()
    {
        using var client = factory.CreateAnonymousClient();
        var id = Guid.NewGuid();

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, "/api/v1/me/profile"),
            (HttpMethod.Put, "/api/v1/me/profile"),
            (HttpMethod.Get, "/api/v1/me/personas"),
            (HttpMethod.Post, "/api/v1/me/personas"),
            (HttpMethod.Patch, $"/api/v1/me/personas/{id}"),
            (HttpMethod.Delete, $"/api/v1/me/personas/{id}"),
            (HttpMethod.Put, $"/api/v1/me/personas/{id}/activate"),
            (HttpMethod.Get, $"/api/v1/me/personas/{id}/style-cards"),
            (HttpMethod.Post, $"/api/v1/me/personas/{id}/swipes"),
            (HttpMethod.Get, $"/api/v1/me/personas/{id}/style"),
            (HttpMethod.Get, "/api/v1/me/sizes"),
            (HttpMethod.Put, "/api/v1/me/sizes"),
            (HttpMethod.Delete, $"/api/v1/me/sizes/{id}"),
            (HttpMethod.Get, $"/api/v1/me/sizes/hint?productId={id}"),
            (HttpMethod.Post, "/api/v1/me/sizes/confirm"),
        })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, url), Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task TheProfileIsCreatedOnFirstReadWithDefaults()
    {
        using var user = await factory.CreateUserAsync(Ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            Assert.False(await db.UserProfiles.AnyAsync(profile => profile.UserId == user.Id, Ct));
        }

        var profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);

        Assert.Equal("Unisex", profile.GetProperty("audience").GetString());
        Assert.Equal(0, profile.GetProperty("personaCount").GetInt32());
        Assert.False(profile.GetProperty("onboardingCompleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("activePersona").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("heightCm").ValueKind);

        await using var after = factory.Services.CreateAsyncScope();
        Assert.True(await after.ServiceProvider.GetRequiredService<KombinDbContext>().UserProfiles.AnyAsync(row => row.UserId == user.Id, Ct));
    }

    [Fact]
    public async Task PuttingTheProfileStoresMeasurementsAndAudience()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var put = await user.Client.PutAsJsonAsync(
            "/api/v1/me/profile",
            new { audience = "women", birthYear = 1995, heightCm = 170, weightKg = 62.5, chestCm = 88, waistCm = 70, hipCm = 95, shoeSizeEu = 38.5 },
            Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        Assert.Equal("Women", profile.GetProperty("audience").GetString());
        Assert.Equal(1995, profile.GetProperty("birthYear").GetInt32());
        Assert.Equal(62.5m, profile.GetProperty("weightKg").GetDecimal());
        Assert.Equal(38.5m, profile.GetProperty("shoeSizeEu").GetDecimal());

        // A second PUT changes what it names and leaves the rest alone.
        using var second = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { heightCm = 175 }, Ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        Assert.Equal("Women", profile.GetProperty("audience").GetString());
        Assert.Equal(175, profile.GetProperty("heightCm").GetInt32());
        Assert.Equal(1995, profile.GetProperty("birthYear").GetInt32());
        Assert.Equal(62.5m, profile.GetProperty("weightKg").GetDecimal());
    }

    [Fact]
    public async Task ALeftOutFieldIsUnchangedAndAnExplicitNullClearsIt()
    {
        using var user = await factory.CreateUserAsync(Ct);

        async Task<JsonElement> PutAsync(object body)
        {
            using var response = await user.Client.PutAsJsonAsync("/api/v1/me/profile", body, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        }

        await PutAsync(new { audience = "Men", birthYear = 1990, heightCm = 180, weightKg = 75.5, chestCm = 100, waistCm = 85, hipCm = 98, shoeSizeEu = 43, city = "Ankara" });

        // An empty body changes nothing.
        var profile = await PutAsync(new { });
        Assert.Equal(1990, profile.GetProperty("birthYear").GetInt32());
        Assert.Equal(180, profile.GetProperty("heightCm").GetInt32());
        Assert.Equal(75.5m, profile.GetProperty("weightKg").GetDecimal());
        Assert.Equal(100, profile.GetProperty("chestCm").GetInt32());
        Assert.Equal(85, profile.GetProperty("waistCm").GetInt32());
        Assert.Equal(98, profile.GetProperty("hipCm").GetInt32());
        Assert.Equal(43m, profile.GetProperty("shoeSizeEu").GetDecimal());
        Assert.Equal("Ankara", profile.GetProperty("city").GetString());

        // Every field can be cleared on its own.
        profile = await PutAsync(new { heightCm = (int?)null, weightKg = (decimal?)null });
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("heightCm").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("weightKg").ValueKind);
        Assert.Equal(1990, profile.GetProperty("birthYear").GetInt32());
        Assert.Equal("Ankara", profile.GetProperty("city").GetString());

        profile = await PutAsync(new { birthYear = (int?)null, chestCm = (int?)null, waistCm = (int?)null, hipCm = (int?)null, shoeSizeEu = (decimal?)null });
        foreach (var field in new[] { "birthYear", "chestCm", "waistCm", "hipCm", "shoeSizeEu" })
        {
            Assert.Equal(JsonValueKind.Null, profile.GetProperty(field).ValueKind);
        }

        Assert.Equal("Ankara", profile.GetProperty("city").GetString());

        // The city too, also with a blank; the audience stays, a profile always has one.
        profile = await PutAsync(new { city = (string?)null, audience = (string?)null });
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("city").ValueKind);
        Assert.Equal("Men", profile.GetProperty("audience").GetString());

        await PutAsync(new { city = "Izmir" });
        profile = await PutAsync(new { city = "  " });
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("city").ValueKind);
    }

    [Fact]
    public async Task OutOfRangeMeasurementsAreRejectedPerField()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { heightCm = 50, shoeSizeEu = 60, audience = "Kids" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ReadProblemAsync(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("heightCm", out _));
        Assert.True(errors.TryGetProperty("shoeSizeEu", out _));
        Assert.True(errors.TryGetProperty("audience", out _));
    }

    [Fact]
    public async Task TheFirstPersonaBecomesActiveAndThePersonaIsListedWithItsStyleSummary()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var office = await user.Client.CreatePersonaAsync("Ofis", new { formalityMin = 3, preferredColors = new List<string> { "navy" } }, Ct);
        var weekend = await user.Client.CreatePersonaAsync("Hafta sonu", cancellationToken: Ct);

        var profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        Assert.Equal(office, profile.GetProperty("activePersona").GetProperty("id").GetGuid());
        Assert.Equal("Ofis", profile.GetProperty("activePersona").GetProperty("name").GetString());
        Assert.Equal(2, profile.GetProperty("personaCount").GetInt32());

        var list = (await user.Client.GetJsonAsync("/api/v1/me/personas", Ct)).GetProperty("items");
        Assert.Equal(["Ofis", "Hafta sonu"], list.EnumerateArray().Select(persona => persona.GetProperty("name").GetString()));
        Assert.Equal([office, weekend], list.EnumerateArray().Select(persona => persona.GetProperty("id").GetGuid()));

        var first = list[0];
        Assert.True(first.GetProperty("isActive").GetBoolean());
        Assert.False(list[1].GetProperty("isActive").GetBoolean());
        Assert.Equal(3, first.GetProperty("formalityMin").GetInt32());
        Assert.Equal(5, first.GetProperty("formalityMax").GetInt32());
        Assert.Equal(["Navy"], Strings(first.GetProperty("preferredColors")));
        Assert.Equal(["Spring", "Summer", "Autumn", "Winter"], Strings(first.GetProperty("seasons")));

        var summary = first.GetProperty("styleSummary");
        Assert.Equal(0, summary.GetProperty("likeCount").GetInt32());
        Assert.Equal(0d, summary.GetProperty("confidence").GetDouble());
        Assert.Equal(["Navy"], Strings(summary.GetProperty("topColors")));
        Assert.Equal(0, summary.GetProperty("topFamilies").GetArrayLength());
    }

    [Fact]
    public async Task AUserMayHaveAtMostSixPersonas()
    {
        using var user = await factory.CreateUserAsync(Ct);

        for (var number = 1; number <= 6; number++)
        {
            await user.Client.CreatePersonaAsync($"Persona {number}", cancellationToken: Ct);
        }

        using var seventh = await user.Client.CreatePersonaRawAsync("Persona 7", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, seventh.StatusCode);
        Assert.Equal("Profile.PersonaLimit", (await seventh.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ArchivingFreesASlotAndTheName()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ids = new List<Guid>();

        for (var number = 1; number <= 6; number++)
        {
            ids.Add(await user.Client.CreatePersonaAsync($"Persona {number}", cancellationToken: Ct));
        }

        using var archive = await user.Client.DeleteAsync($"/api/v1/me/personas/{ids[5]}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        // Same name as the archived one, and a sixth non-archived persona again.
        await user.Client.CreatePersonaAsync("Persona 6", cancellationToken: Ct);
        Assert.Equal(6, (await user.Client.GetJsonAsync("/api/v1/me/personas", Ct)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task PersonaNamesAreUniquePerUserButNotAcrossUsers()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        using var duplicate = await user.Client.CreatePersonaRawAsync("  Ofis ", cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("Profile.PersonaNameTaken", (await duplicate.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        await other.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        var gece = await user.Client.CreatePersonaAsync("Gece", cancellationToken: Ct);
        using var rename = await user.Client.PatchJsonAsync($"/api/v1/me/personas/{gece}", new { name = "Ofis" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
    }

    [Fact]
    public async Task InvalidPersonasAreRejectedPerField()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.CreatePersonaRawAsync("", new { formalityMin = 9, preferredColors = new List<string> { "Chartreuse" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ReadProblemAsync(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("formalityMin", out _));
        Assert.True(errors.TryGetProperty("preferredColors", out _));
    }

    [Fact]
    public async Task ArchivingTheActivePersonaHandsOverToTheFirstRemainingOne()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var first = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        var second = await user.Client.CreatePersonaAsync("Gece", cancellationToken: Ct);
        var third = await user.Client.CreatePersonaAsync("Spor", cancellationToken: Ct);

        using var archive = await user.Client.DeleteAsync($"/api/v1/me/personas/{first}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        var profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        Assert.Equal(second, profile.GetProperty("activePersona").GetProperty("id").GetGuid());
        Assert.Equal(2, profile.GetProperty("personaCount").GetInt32());

        // Archiving one that is not active changes nothing.
        using var archiveThird = await user.Client.DeleteAsync($"/api/v1/me/personas/{third}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, archiveThird.StatusCode);
        profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        Assert.Equal(second, profile.GetProperty("activePersona").GetProperty("id").GetGuid());

        using var archiveSecond = await user.Client.DeleteAsync($"/api/v1/me/personas/{second}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, archiveSecond.StatusCode);
        profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("activePersona").ValueKind);
        Assert.Equal(0, profile.GetProperty("personaCount").GetInt32());
    }

    [Fact]
    public async Task ArchivedPersonasKeepTheirSwipesButCannotBeUsedAnymore()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        await user.Client.SwipeAsync(persona, StyleCards.Take(3).Select(card => (card.Id, "Like")), Ct);

        using var archive = await user.Client.DeleteAsync($"/api/v1/me/personas/{persona}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            Assert.Equal(3, await db.StyleSwipes.CountAsync(swipe => swipe.PersonaId == persona, Ct));
        }

        foreach (var url in new[] { $"/api/v1/me/personas/{persona}/style", $"/api/v1/me/personas/{persona}/style-cards" })
        {
            using var response = await user.Client.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var again = await user.Client.DeleteAsync($"/api/v1/me/personas/{persona}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("Profile.PersonaNotFound", (await again.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ActivatingAPersonaSwitchesTheActiveOneAndForeignPersonasAreInvisible()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        var night = await user.Client.CreatePersonaAsync("Gece", cancellationToken: Ct);
        var foreign = await stranger.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        using var activate = await user.Client.PutAsync($"/api/v1/me/personas/{night}/activate", content: null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        var profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        Assert.Equal(night, profile.GetProperty("activePersona").GetProperty("id").GetGuid());

        using var steal = await user.Client.PutAsync($"/api/v1/me/personas/{foreign}/activate", content: null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Patch, $"/api/v1/me/personas/{foreign}"),
            (HttpMethod.Delete, $"/api/v1/me/personas/{foreign}"),
            (HttpMethod.Get, $"/api/v1/me/personas/{foreign}/style"),
            (HttpMethod.Get, $"/api/v1/me/personas/{foreign}/style-cards"),
        })
        {
            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Patch)
            {
                request.Content = JsonContent.Create(new { name = "Mine" });
            }

            using var response = await user.Client.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {url} returned {(int)response.StatusCode}");
        }

        using var swipe = await user.Client.SwipeRawAsync(foreign, [(StyleCards[0].Id, "Like")], Ct);
        Assert.Equal(HttpStatusCode.NotFound, swipe.StatusCode);
    }

    [Fact]
    public async Task PatchingAPersonaChangesOnlyWhatIsGivenAndRecomputesTheStyleWhenPreferencesChange()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", new { budgetMin = 100, budgetMax = 900 }, Ct);
        await user.Client.SwipeAsync(persona, CardsWithColor("Black").Take(6).Select(card => (card, "Like")), Ct);

        var before = await user.Client.GetJsonAsync($"/api/v1/me/personas/{persona}/style", Ct);
        Assert.Equal(1d, before.GetProperty("colorWeights").GetProperty("Black").GetDouble());

        using var avoid = await user.Client.PatchJsonAsync($"/api/v1/me/personas/{persona}", new { avoidedColors = new List<string> { "black" }, preferredColors = new List<string> { "Navy" }, icon = "moon" }, Ct);
        Assert.Equal(HttpStatusCode.OK, avoid.StatusCode);
        var patched = await avoid.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("moon", patched.GetProperty("icon").GetString());
        Assert.Equal("Ofis", patched.GetProperty("name").GetString());
        Assert.Equal(900m, patched.GetProperty("budgetMax").GetDecimal());
        Assert.Equal(["Black"], Strings(patched.GetProperty("avoidedColors")));

        var after = await user.Client.GetJsonAsync($"/api/v1/me/personas/{persona}/style", Ct);
        Assert.False(after.GetProperty("colorWeights").TryGetProperty("Black", out _));
        Assert.True(after.GetProperty("colorWeights").TryGetProperty("Navy", out _));
        Assert.Equal(6, after.GetProperty("likeCount").GetInt32());
    }

    [Fact]
    public async Task PatchRejectsConflictsBetweenGivenAndStoredValues()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", new { budgetMin = 100, budgetMax = 900, preferredColors = new List<string> { "Navy" }, formalityMin = 2, formalityMax = 4 }, Ct);

        foreach (var body in new object[]
        {
            new { budgetMin = 1000 },
            new { formalityMax = 1 },
            new { avoidedColors = new List<string> { "Navy" } },
            new { },
            new { seasons = Array.Empty<string>() },
        })
        {
            using var response = await user.Client.PatchJsonAsync($"/api/v1/me/personas/{persona}", body, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(body)} returned {(int)response.StatusCode}");
        }

        using var clear = await user.Client.PatchJsonAsync($"/api/v1/me/personas/{persona}", new { clearBudget = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var cleared = await clear.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("budgetMin").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("budgetMax").ValueKind);
    }

    [Fact]
    public async Task StyleCardsAreSeededAfterStartup()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

        // The shared test database never runs the dev seed, but style cards are content: they are there in every environment.
        var stored = await db.StyleCards.AsNoTracking().ToListAsync(Ct);

        Assert.Equal(StyleCards.Count, stored.Count);
        Assert.Equal(StyleCards.Select(card => card.Id).Order(), stored.Select(card => card.Id).Order());
    }

    [Fact]
    public async Task TheFeedStartsWithEveryFamilyAndExcludesSwipedCards()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        var feed = await user.Client.GetCardsAsync(persona, cancellationToken: Ct);
        var items = feed.GetProperty("items").EnumerateArray().ToList();

        // A profile without a stated audience (Unisex) sees all 40 cards; 20 come back, the rest remain.
        Assert.Equal(20, items.Count);
        Assert.Equal(StyleCards.Count - 20, feed.GetProperty("remaining").GetInt32());
        Assert.Equal(8, items.Take(8).Select(item => item.GetProperty("family").GetString()).Distinct().Count());
        Assert.All(items, item =>
        {
            Assert.False(string.IsNullOrEmpty(item.GetProperty("title").GetString()));
            Assert.StartsWith("https://picsum.photos/", item.GetProperty("imageUrl").GetString(), StringComparison.Ordinal);
        });

        var swiped = items.Take(5).Select(item => item.GetProperty("id").GetGuid()).ToList();
        await user.Client.SwipeAsync(persona, swiped.Select((id, index) => (id, index % 2 == 0 ? "Like" : "Skip")), Ct);

        var next = await user.Client.GetCardsAsync(persona, 50, Ct);
        var nextIds = next.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(StyleCards.Count - 5, nextIds.Count);
        Assert.Empty(nextIds.Intersect(swiped));
        Assert.Equal(0, next.GetProperty("remaining").GetInt32());

        // Another persona of the same user has swiped nothing.
        var other = await user.Client.CreatePersonaAsync("Gece", cancellationToken: Ct);
        Assert.Equal(20, (await user.Client.GetCardsAsync(other, cancellationToken: Ct)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AUnisexProfileGetsWomenMenAndUnisexCardsInRotation()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        var feed = await user.Client.GetCardsAsync(persona, cancellationToken: Ct);
        var audiences = feed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("audience").GetString()).ToList();

        Assert.Equal(3, audiences.Take(12).Distinct().Count());
        Assert.True(audiences.Take(6).Distinct().Count() >= 2);
        Assert.Equal(["Women", "Men", "Unisex"], audiences.Take(3));
    }

    [Theory]
    [InlineData("Women", new[] { "Women", "Unisex" }, 24)]
    [InlineData("Men", new[] { "Men", "Unisex" }, 24)]
    [InlineData("Unisex", new[] { "Women", "Men", "Unisex" }, 40)]
    public async Task TheFeedRespectsTheProfileAudience(string audience, string[] allowed, int expectedCount)
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var put = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { audience }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        var feed = await user.Client.GetCardsAsync(persona, 50, Ct);
        var items = feed.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(expectedCount, items.Count);
        Assert.All(items, item => Assert.Contains(item.GetProperty("audience").GetString(), allowed));
        Assert.Equal(8, items.Take(8).Select(item => item.GetProperty("family").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task TheFeedLimitIsClamped()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        Assert.Equal(1, (await user.Client.GetCardsAsync(persona, 0, Ct)).GetProperty("items").GetArrayLength());
        Assert.Equal(5, (await user.Client.GetCardsAsync(persona, 5, Ct)).GetProperty("items").GetArrayLength());
        Assert.Equal(40, (await user.Client.GetCardsAsync(persona, 500, Ct)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ASwipeBatchRecomputesTheStyleAndCompletesOnboardingAfterEightLikes()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        var cards = StyleCards.Select(card => card.Id).ToList();

        var first = await user.Client.SwipeAsync(persona, cards.Take(7).Select(card => (card, "Like")), Ct);
        Assert.False(first.GetProperty("onboardingCompleted").GetBoolean());
        Assert.Equal(7, first.GetProperty("styleSummary").GetProperty("likeCount").GetInt32());
        Assert.Equal(0.58, first.GetProperty("styleSummary").GetProperty("confidence").GetDouble());
        Assert.False((await user.Client.GetJsonAsync("/api/v1/me/profile", Ct)).GetProperty("onboardingCompleted").GetBoolean());

        var second = await user.Client.SwipeAsync(persona, [(cards[7], "Like"), (cards[8], "Dislike"), (cards[9], "Skip")], Ct);
        Assert.True(second.GetProperty("onboardingCompleted").GetBoolean());
        var summary = second.GetProperty("styleSummary");
        Assert.Equal(8, summary.GetProperty("likeCount").GetInt32());
        Assert.Equal(0.67, summary.GetProperty("confidence").GetDouble());
        Assert.Equal(3, summary.GetProperty("topColors").GetArrayLength());
        Assert.Equal(2, summary.GetProperty("topFamilies").GetArrayLength());
        Assert.InRange(summary.GetProperty("formalityMean").GetDecimal(), 1m, 5m);

        var profile = await user.Client.GetJsonAsync("/api/v1/me/profile", Ct);
        Assert.True(profile.GetProperty("onboardingCompleted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, profile.GetProperty("onboardingCompletedAt").ValueKind);

        var style = await user.Client.GetJsonAsync($"/api/v1/me/personas/{persona}/style", Ct);
        Assert.Equal(8, style.GetProperty("likeCount").GetInt32());
        Assert.Equal(1, style.GetProperty("dislikeCount").GetInt32());
        Assert.Equal(persona, style.GetProperty("personaId").GetGuid());
        Assert.Contains(style.GetProperty("colorWeights").EnumerateObject(), color => color.Value.GetDouble() == 1d);
        Assert.True(style.GetProperty("familyWeights").EnumerateObject().Any());
        Assert.True(style.GetProperty("fitWeights").EnumerateObject().Any());
        Assert.True(style.GetProperty("seasonWeights").EnumerateObject().Any());
    }

    [Fact]
    public async Task OnboardingCompletionIsSetOnceAndNotMovedByLaterSwipes()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        var cards = StyleCards.Select(card => card.Id).ToList();

        await user.Client.SwipeAsync(persona, cards.Take(8).Select(card => (card, "Like")), Ct);
        var completedAt = (await user.Client.GetJsonAsync("/api/v1/me/profile", Ct)).GetProperty("onboardingCompletedAt").GetDateTimeOffset();

        await user.Client.SwipeAsync(persona, cards.Skip(8).Take(4).Select(card => (card, "Dislike")), Ct);

        Assert.Equal(completedAt, (await user.Client.GetJsonAsync("/api/v1/me/profile", Ct)).GetProperty("onboardingCompletedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task SwipingACardAgainReplacesTheDecision()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        var card = StyleCards[0].Id;
        var other = StyleCards[1].Id;

        var liked = await user.Client.SwipeAsync(persona, [(card, "Like"), (other, "Like")], Ct);
        Assert.Equal(2, liked.GetProperty("styleSummary").GetProperty("likeCount").GetInt32());

        var changed = await user.Client.SwipeAsync(persona, [(card, "Dislike")], Ct);
        Assert.Equal(1, changed.GetProperty("styleSummary").GetProperty("likeCount").GetInt32());

        var style = await user.Client.GetJsonAsync($"/api/v1/me/personas/{persona}/style", Ct);
        Assert.Equal(1, style.GetProperty("dislikeCount").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        var swipes = await db.StyleSwipes.AsNoTracking().Where(swipe => swipe.PersonaId == persona).ToListAsync(Ct);
        Assert.Equal(2, swipes.Count);
        Assert.Equal(Kombin.Api.Persistence.Entities.Profile.SwipeDecision.Dislike, swipes.Single(swipe => swipe.StyleCardId == card).Decision);
    }

    [Fact]
    public async Task TheLastDecisionForACardInOneBatchWins()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        var result = await user.Client.SwipeAsync(persona, [(StyleCards[0].Id, "Dislike"), (StyleCards[0].Id, "Like")], Ct);

        Assert.Equal(1, result.GetProperty("styleSummary").GetProperty("likeCount").GetInt32());
    }

    [Fact]
    public async Task AnUnknownStyleCardFailsTheWholeBatch()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        using var response = await user.Client.SwipeRawAsync(persona, [(StyleCards[0].Id, "Like"), (Guid.NewGuid(), "Like")], Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Profile.StyleCardNotFound", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Equal(0, (await user.Client.GetJsonAsync($"/api/v1/me/personas/{persona}/style", Ct)).GetProperty("likeCount").GetInt32());
    }

    [Fact]
    public async Task InvalidSwipeBatchesAreRejected()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        using var empty = await user.Client.SwipeRawAsync(persona, [], Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var badDecision = await user.Client.SwipeRawAsync(persona, [(StyleCards[0].Id, "Love")], Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badDecision.StatusCode);

        using var tooMany = await user.Client.SwipeRawAsync(persona, Enumerable.Range(0, 51).Select(_ => (StyleCards[0].Id, "Like")), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task APersonaWithoutSwipesHasAnEmptyButReadableStyle()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        var style = await user.Client.GetJsonAsync($"/api/v1/me/personas/{persona}/style", Ct);

        Assert.Equal(0, style.GetProperty("likeCount").GetInt32());
        Assert.Equal(0d, style.GetProperty("confidence").GetDouble());
        Assert.Empty(style.GetProperty("colorWeights").EnumerateObject());
        Assert.Equal(0m, style.GetProperty("formalityMean").GetDecimal());
    }
}
