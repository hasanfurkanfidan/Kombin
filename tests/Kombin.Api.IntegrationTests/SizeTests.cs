using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class SizeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PutSizeAsync(TestUser user, SeededProduct product, string size, string group = "Tops") =>
        user.Client.PutAsJsonAsync("/api/v1/me/sizes", new { brandSlug = Slug(product.Brand), group, size }, Ct);

    private static async Task<JsonElement> HintAsync(TestUser user, SeededProduct product) =>
        await user.Client.GetJsonAsync($"/api/v1/me/sizes/hint?productId={product.ProductId}", Ct);

    private static async Task<JsonElement> SizesAsync(TestUser user) =>
        (await user.Client.GetJsonAsync("/api/v1/me/sizes", Ct)).GetProperty("items");

    [Fact]
    public async Task EnteringASizeStoresItNormalizedAndReplacesItForTheSameBrandAndGroup()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);

        using var first = await PutSizeAsync(user, product, " m ");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var entry = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("M", entry.GetProperty("size").GetString());
        Assert.Equal("UserEntered", entry.GetProperty("source").GetString());
        Assert.Equal(1m, entry.GetProperty("confidence").GetDecimal());
        Assert.Equal("Tops", entry.GetProperty("group").GetString());
        Assert.Equal(Slug(product.Brand), entry.GetProperty("brand").GetProperty("slug").GetString());

        using var second = await PutSizeAsync(user, product, "L");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(entry.GetProperty("id").GetGuid(), (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());

        // Another group of the same brand is a separate entry.
        using var bottoms = await PutSizeAsync(user, product, "32", "Bottoms");
        Assert.Equal(HttpStatusCode.OK, bottoms.StatusCode);

        var items = (await SizesAsync(user)).EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("L", items.Single(item => item.GetProperty("group").GetString() == "Tops").GetProperty("size").GetString());
    }

    [Fact]
    public async Task SizesBelongToTheUserAndCanBeDeleted()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        using var created = await PutSizeAsync(user, product, "M");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        Assert.Equal(0, (await SizesAsync(stranger)).GetArrayLength());

        using var steal = await stranger.Client.DeleteAsync($"/api/v1/me/sizes/{id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        Assert.Equal("Profile.SizeEntryNotFound", (await steal.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var delete = await user.Client.DeleteAsync($"/api/v1/me/sizes/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0, (await SizesAsync(user)).GetArrayLength());

        using var again = await user.Client.DeleteAsync($"/api/v1/me/sizes/{id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task AnUnknownBrandIsNotFoundAndBadInputIsRejected()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var unknown = await user.Client.PutAsJsonAsync("/api/v1/me/sizes", new { brandSlug = "no-such-brand", group = "Tops", size = "M" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Catalog.BrandNotFound", (await unknown.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var invalid = await user.Client.PutAsJsonAsync("/api/v1/me/sizes", new { brandSlug = "x", group = "Hats", size = "" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.ReadProblemAsync(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("group", out _));
        Assert.True(errors.TryGetProperty("size", out _));
    }

    [Fact]
    public async Task TheHintReturnsTheUsersOwnEntryForTheProductsBrandAndGroup()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        using var created = await PutSizeAsync(user, product, "M");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var hint = await HintAsync(user, product);

        Assert.Equal("Tops", hint.GetProperty("group").GetString());
        Assert.Equal("M", hint.GetProperty("size").GetString());
        Assert.Equal("UserEntered", hint.GetProperty("source").GetString());
        Assert.Equal(1m, hint.GetProperty("confidence").GetDecimal());
        Assert.Equal(JsonValueKind.Null, hint.GetProperty("suggestion").ValueKind);
    }

    [Fact]
    public async Task TwoAgreeingSizesAtOtherBrandsBecomeAnInferredSuggestionThatIsNotStored()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var first = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        var second = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        var third = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);

        using var one = await PutSizeAsync(user, first, "M");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);

        // One entry is not enough to infer anything.
        var alone = await HintAsync(user, third);
        Assert.Equal(JsonValueKind.Null, alone.GetProperty("size").ValueKind);
        Assert.Equal(JsonValueKind.Null, alone.GetProperty("suggestion").ValueKind);

        using var two = await PutSizeAsync(user, second, "m");
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);

        var hint = await HintAsync(user, third);
        Assert.Equal("Tops", hint.GetProperty("group").GetString());
        Assert.Equal(JsonValueKind.Null, hint.GetProperty("size").ValueKind);
        Assert.Equal(JsonValueKind.Null, hint.GetProperty("source").ValueKind);
        var suggestion = hint.GetProperty("suggestion");
        Assert.Equal("M", suggestion.GetProperty("size").GetString());
        Assert.Equal("Inferred", suggestion.GetProperty("source").GetString());
        Assert.InRange(suggestion.GetProperty("confidence").GetDecimal(), 0.1m, 0.8m);

        // Never persisted: the list still holds only the two entered sizes.
        Assert.Equal(2, (await SizesAsync(user)).GetArrayLength());
    }

    [Fact]
    public async Task OnlyEntriesOfTheSameGroupCountForTheInference()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var first = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        var second = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        var third = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        (await PutSizeAsync(user, first, "M", "Bottoms")).Dispose();
        (await PutSizeAsync(user, second, "M", "Shoes")).Dispose();

        var hint = await HintAsync(user, third);

        Assert.Equal(JsonValueKind.Null, hint.GetProperty("suggestion").ValueKind);
    }

    [Fact]
    public async Task TheHintForAnUnknownProductIsNotFound()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.GetAsync($"/api/v1/me/sizes/hint?productId={Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Catalog.ProductNotFound", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ConfirmingThatTheSizeDidNotFitHalvesTheConfidenceButKeepsTheEntry()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        (await PutSizeAsync(user, product, "M")).Dispose();

        using var unrelated = await user.Client.PostAsJsonAsync("/api/v1/me/sizes/confirm", new { productId = product.ProductId, size = "L", fitted = false }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, unrelated.StatusCode);
        Assert.Equal(1m, (await HintAsync(user, product)).GetProperty("confidence").GetDecimal());

        using var response = await user.Client.PostAsJsonAsync("/api/v1/me/sizes/confirm", new { productId = product.ProductId, size = "m", fitted = false }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var hint = await HintAsync(user, product);
        Assert.Equal("M", hint.GetProperty("size").GetString());
        Assert.Equal("UserEntered", hint.GetProperty("source").GetString());
        Assert.Equal(0.5m, hint.GetProperty("confidence").GetDecimal());
        Assert.Equal(1, (await SizesAsync(user)).GetArrayLength());
    }

    [Fact]
    public async Task ConfirmingThatTheSizeDidNotFitWithoutAnEntryDoesNothing()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);

        using var response = await user.Client.PostAsJsonAsync("/api/v1/me/sizes/confirm", new { productId = product.ProductId, size = "M", fitted = false }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, (await SizesAsync(user)).GetArrayLength());
    }

    [Fact]
    public async Task ConfirmingThatTheSizeFittedStoresItAsConfirmedWithFullConfidence()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);

        using var created = await user.Client.PostAsJsonAsync("/api/v1/me/sizes/confirm", new { productId = product.ProductId, size = "l", fitted = true }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);

        var hint = await HintAsync(user, product);
        Assert.Equal("L", hint.GetProperty("size").GetString());
        Assert.Equal("Confirmed", hint.GetProperty("source").GetString());
        Assert.Equal(1m, hint.GetProperty("confidence").GetDecimal());

        // Doubted first, then confirmed again with another size: the entry is restored and replaced.
        using var doubted = await user.Client.PostAsJsonAsync("/api/v1/me/sizes/confirm", new { productId = product.ProductId, size = "L", fitted = false }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, doubted.StatusCode);
        Assert.Equal(0.5m, (await HintAsync(user, product)).GetProperty("confidence").GetDecimal());

        using var replaced = await user.Client.PostAsJsonAsync("/api/v1/me/sizes/confirm", new { productId = product.ProductId, size = "XL", fitted = true }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        hint = await HintAsync(user, product);
        Assert.Equal("XL", hint.GetProperty("size").GetString());
        Assert.Equal(1m, hint.GetProperty("confidence").GetDecimal());
        Assert.Equal(1, (await SizesAsync(user)).GetArrayLength());
    }

    [Fact]
    public async Task ConfirmNeedsAnExplicitFlagAndAKnownProduct()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);

        using var missingFlag = await user.Client.PostAsJsonAsync("/api/v1/me/sizes/confirm", new { productId = product.ProductId, size = "M" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingFlag.StatusCode);

        using var unknown = await user.Client.PostAsJsonAsync("/api/v1/me/sizes/confirm", new { productId = Guid.NewGuid(), size = "M", fitted = true }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
