using System.Net;
using System.Text.Json;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;
using static Kombin.Api.IntegrationTests.ProfileClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class PersonaMatchTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Three products that differ only in color, with a shared word in the name so a search finds exactly them.</summary>
    private static async Task<(string Token, Guid BlackId)> SeedColorsAsync(HttpClient client)
    {
        var token = Unique("match");
        var brand = Unique("Mch");

        await client.UpsertOkAsync(
            $"{Slug(brand)}-store",
            [
                Item($"m-{Guid.NewGuid():N}", 300m, brand, $"{token} Black Blazer", color: "Black"),
                Item($"m-{Guid.NewGuid():N}", 300m, brand, $"{token} Navy Blazer", color: "Navy"),
                Item($"m-{Guid.NewGuid():N}", 300m, brand, $"{token} Red Blazer", color: "Red"),
            ],
            Ct);

        var list = await client.GetJsonAsync($"/api/v1/catalog/products?query={token}", Ct);
        var black = list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString()!.Contains("Black", StringComparison.Ordinal));

        return (token, black.GetProperty("id").GetGuid());
    }

    private static string[] Names(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()![(item.GetProperty("name").GetString()!.IndexOf(' ', StringComparison.Ordinal) + 1)..])];

    /// <summary>A persona that has liked every card containing Black and disliked those containing Red.</summary>
    private static async Task<Guid> BlackLoverAsync(TestUser user)
    {
        var persona = await user.Client.CreatePersonaAsync("Siyah", cancellationToken: Ct);

        await user.Client.SwipeAsync(persona, CardsWithColor("Black").Take(12).Select(card => (card, "Like")), Ct);
        await user.Client.SwipeAsync(persona, CardsWithColor("Red").Except(CardsWithColor("Black")).Select(card => (card, "Dislike")), Ct);

        return persona;
    }

    [Fact]
    public async Task ListingWithAPersonaScoresEveryItemAndSortsByMatch()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (token, _) = await SeedColorsAsync(user.Client);
        var persona = await BlackLoverAsync(user);

        var page = await user.Client.GetJsonAsync($"/api/v1/catalog/products?query={token}&personaId={persona}&sort=match", Ct);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        var scores = items.Select(item => item.GetProperty("matchScore").GetInt32()).ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(scores.OrderDescending(), scores);
        Assert.Equal("Black Blazer", Names(page)[0]);
        Assert.True(scores[0] > scores[^1], string.Join(",", scores));
        Assert.All(scores, score => Assert.InRange(score, 0, 100));

        var red = items.Single(item => item.GetProperty("name").GetString()!.Contains("Red", StringComparison.Ordinal));
        Assert.True(red.GetProperty("matchScore").GetInt32() < scores[0]);
    }

    [Fact]
    public async Task OtherSortsStillCarryScoresAndWithoutAPersonaThereAreNone()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (token, _) = await SeedColorsAsync(user.Client);
        var persona = await BlackLoverAsync(user);

        var priced = await user.Client.GetJsonAsync($"/api/v1/catalog/products?query={token}&personaId={persona}&sort=priceAsc", Ct);
        Assert.All(priced.GetProperty("items").EnumerateArray(), item => Assert.NotEqual(JsonValueKind.Null, item.GetProperty("matchScore").ValueKind));

        var plain = await user.Client.GetJsonAsync($"/api/v1/catalog/products?query={token}", Ct);
        Assert.All(plain.GetProperty("items").EnumerateArray(), item => Assert.False(item.TryGetProperty("matchScore", out _)));
    }

    [Fact]
    public async Task MatchSortPagesInMemoryAndKeepsTheTotal()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (token, _) = await SeedColorsAsync(user.Client);
        var persona = await BlackLoverAsync(user);
        var url = $"/api/v1/catalog/products?query={token}&personaId={persona}&sort=match&pageSize=2";

        var first = await user.Client.GetJsonAsync($"{url}&page=1", Ct);
        var second = await user.Client.GetJsonAsync($"{url}&page=2", Ct);

        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(3, Names(first).Concat(Names(second)).Distinct().Count());
    }

    [Fact]
    public async Task AnotherUsersPersonaIsNotFound()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        var (token, productId) = await SeedColorsAsync(owner.Client);
        var persona = await owner.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        foreach (var url in new[]
        {
            $"/api/v1/catalog/products?query={token}&personaId={persona}&sort=match",
            $"/api/v1/catalog/products?personaId={persona}",
            $"/api/v1/catalog/products/{productId}?personaId={persona}",
        })
        {
            using var response = await stranger.Client.GetAsync(url, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url} returned {(int)response.StatusCode}");
            Assert.Equal("Profile.PersonaNotFound", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using var unknown = await owner.Client.GetAsync($"/api/v1/catalog/products?personaId={Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AnArchivedPersonaCannotScore()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        using var archive = await user.Client.DeleteAsync($"/api/v1/me/personas/{persona}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        using var response = await user.Client.GetAsync($"/api/v1/catalog/products?personaId={persona}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCallerCannotUseAPersonaId()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        var (_, productId) = await SeedColorsAsync(owner.Client);
        var persona = await owner.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        using var anonymous = factory.CreateAnonymousClient();

        foreach (var url in new[] { $"/api/v1/catalog/products?personaId={persona}", $"/api/v1/catalog/products/{productId}?personaId={persona}" })
        {
            using var response = await anonymous.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Profile.PersonaRequiresAuth", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }
    }

    [Fact]
    public async Task SortingByMatchNeedsAPersona()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.GetAsync("/api/v1/catalog/products?sort=match", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("personaId", out _));
    }

    [Fact]
    public async Task TheDetailCarriesTheMatchScoreAndTheSizeHintForASignedInUser()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (token, blackId) = await SeedColorsAsync(user.Client);
        var persona = await BlackLoverAsync(user);

        var detail = await user.Client.GetJsonAsync($"/api/v1/catalog/products/{blackId}?personaId={persona}", Ct);
        var list = await user.Client.GetJsonAsync($"/api/v1/catalog/products?query={token}&personaId={persona}", Ct);
        var listed = list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == blackId);

        Assert.Equal(listed.GetProperty("matchScore").GetInt32(), detail.GetProperty("matchScore").GetInt32());
        Assert.Equal("Tops", detail.GetProperty("sizeHint").GetProperty("group").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("sizeHint").GetProperty("size").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("sizeHint").GetProperty("suggestion").ValueKind);

        var withoutPersona = await user.Client.GetJsonAsync($"/api/v1/catalog/products/{blackId}", Ct);
        Assert.False(withoutPersona.TryGetProperty("matchScore", out _));
        Assert.Equal("Tops", withoutPersona.GetProperty("sizeHint").GetProperty("group").GetString());
    }

    [Fact]
    public async Task TheDetailOfAnAnonymousCallerHasNeitherScoreNorSizeHint()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        var (_, blackId) = await SeedColorsAsync(owner.Client);
        using var anonymous = factory.CreateAnonymousClient();

        var detail = await anonymous.GetJsonAsync($"/api/v1/catalog/products/{blackId}", Ct);

        Assert.False(detail.TryGetProperty("matchScore", out _));
        Assert.False(detail.TryGetProperty("sizeHint", out _));
    }

    [Fact]
    public async Task AnUntrainedPersonaNeverShowsMoreThanSixty()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (token, _) = await SeedColorsAsync(user.Client);
        var persona = await user.Client.CreatePersonaAsync("Yeni", new { preferredColors = new List<string> { "Black", "Navy", "Red" } }, Ct);

        var page = await user.Client.GetJsonAsync($"/api/v1/catalog/products?query={token}&personaId={persona}", Ct);

        Assert.All(page.GetProperty("items").EnumerateArray(), item => Assert.InRange(item.GetProperty("matchScore").GetInt32(), 0, 60));
    }

    [Fact]
    public async Task ADetailOfferIsOpenedThroughTheRedirectPath()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 400m, cancellationToken: Ct);

        var detail = await user.Client.GetJsonAsync($"/api/v1/catalog/products/{product.ProductId}", Ct);
        var offer = detail.GetProperty("offers")[0];

        Assert.Equal($"/api/v1/go/{product.OfferId}", offer.GetProperty("goPath").GetString());
        Assert.DoesNotContain("shop.example.com", detail.GetRawText(), StringComparison.Ordinal);

        using var go = await user.Client.GetAsync(offer.GetProperty("goPath").GetString(), Ct);
        Assert.Equal(HttpStatusCode.Redirect, go.StatusCode);
    }

    [Fact]
    public async Task PersonaIdsThatAreNotGuidsAreABadRequest()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.GetAsync("/api/v1/catalog/products?personaId=not-a-guid", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
