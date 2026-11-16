using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>"Aldın mı? Beden uydu mu?": the questions a signed-in user's clicks on <c>/go</c> earn, their answers and what they do to the size map.</summary>
public sealed class SizePromptTests(ApiFactory factory)
{
    private const string Prompts = "/api/v1/me/size-prompts";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task GoAsync(HttpClient client, Guid offerId)
    {
        using var response = await client.GetAsync($"/api/v1/go/{offerId}?src=product_detail", Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>Moves the clicks the user made on a product back in time (the table is append-only for the app, the test owns the clock).</summary>
    private Task<int> AgeClicksAsync(Guid userId, Guid productId, double days) =>
        factory.WithDbAsync(db => db.Clicks
            .Where(click => click.UserId == userId && click.ProductId == productId)
            .ExecuteUpdateAsync(set => set.SetProperty(click => click.ClickedAt, DateTimeOffset.UtcNow.AddDays(-days)), Ct));

    private static async Task<List<JsonElement>> OpenAsync(HttpClient client) =>
        [.. (await client.GetJsonAsync(Prompts, Ct)).GetProperty("prompts").EnumerateArray()];

    private static async Task<HttpResponseMessage> AnswerAsync(HttpClient client, Guid promptId, string answer, string? size = null) =>
        await client.PostAsJsonAsync($"{Prompts}/{promptId}", new { answer, size }, Ct);

    private static async Task<JsonElement> SizesAsync(HttpClient client) =>
        (await client.GetJsonAsync("/api/v1/me/sizes", Ct)).GetProperty("items");

    /// <summary>A product the user clicked through to, five days ago, with its open question.</summary>
    private async Task<(SeededProduct Product, Guid PromptId)> ClickedProductAsync(TestUser user, double days = 5)
    {
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        await GoAsync(user.Client, product.OfferId);
        await AgeClicksAsync(user.Id, product.ProductId, days);
        var open = await OpenAsync(user.Client);

        return (product, open.Single(prompt => prompt.GetProperty("product").GetProperty("id").GetGuid() == product.ProductId).GetProperty("id").GetGuid());
    }

    /// <summary>Another product of the same brand and seller, so the size map entry is shared.</summary>
    private static async Task<Guid> SecondProductOfBrandAsync(TestUser user, SeededProduct first)
    {
        var body = await user.Client.UpsertOkAsync(first.SellerSlug, [Item($"eng-{Guid.NewGuid():N}", 350m, first.Brand, "Ceket", modelCode: Unique("sec"))], Ct);
        var result = body.GetProperty("results")[0];
        await GoAsync(user.Client, result.GetProperty("offerId").GetGuid());

        return result.GetProperty("productId").GetGuid();
    }

    [Fact]
    public async Task AQuestionOpensThreeDaysAfterTheClickAndNamesTheProductBrandAndSeller()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 300m, sizes: ["S", "M", "L"], cancellationToken: Ct);

        await GoAsync(user.Client, product.OfferId);
        Assert.Empty(await OpenAsync(user.Client));

        await AgeClicksAsync(user.Id, product.ProductId, 2.9);
        Assert.Empty(await OpenAsync(user.Client));

        await AgeClicksAsync(user.Id, product.ProductId, 3.01);
        var prompt = Assert.Single(await OpenAsync(user.Client));
        Assert.Equal(product.ProductId, prompt.GetProperty("product").GetProperty("id").GetGuid());
        Assert.Equal(product.Brand, prompt.GetProperty("brand").GetProperty("name").GetString());
        Assert.Equal(product.SellerSlug, prompt.GetProperty("seller").GetProperty("slug").GetString());
        Assert.Equal("Tops", prompt.GetProperty("group").GetString());
        Assert.Equal(JsonValueKind.Null, prompt.GetProperty("knownSize").ValueKind);
        Assert.Equal(["S", "M", "L"], prompt.GetProperty("availableSizes").EnumerateArray().Select(size => size.GetString()));
        Assert.Equal(prompt.GetProperty("clickedAt").GetDateTimeOffset().AddDays(30), prompt.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.False(prompt.GetRawText().Contains("shop.example.com", StringComparison.Ordinal), "a question never carries the seller's raw URL");

        // Asking again changes nothing, and a second click on the product is not a second question.
        await GoAsync(user.Client, product.OfferId);
        Assert.Single(await OpenAsync(user.Client));
        Assert.Equal(1, await factory.WithDbAsync(db => db.SizePrompts.CountAsync(row => row.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task OnlyTheClicksOfASignedInUserCountAndNobodySeesAnothersQuestions()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var (_, promptId) = await ClickedProductAsync(user);
        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);

        // An anonymous click has nobody to ask.
        await GoAsync(anonymous, product.OfferId);
        await factory.WithDbAsync(db => db.Clicks.Where(click => click.OfferId == product.OfferId).ExecuteUpdateAsync(set => set.SetProperty(click => click.ClickedAt, DateTimeOffset.UtcNow.AddDays(-5)), Ct));

        Assert.Single(await OpenAsync(user.Client));
        Assert.Empty(await OpenAsync(stranger.Client));
        using var steal = await AnswerAsync(stranger.Client, promptId, "NeverAsk");
        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        Assert.Equal("Profile.SizePromptNotFound", (await steal.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Single(await OpenAsync(user.Client));

        using var signedOut = await anonymous.GetAsync(Prompts, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task TheNewestQuestionsComeFirstThreeAtATimeAndAnAnswerBringsTheNextOne()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var products = new List<SeededProduct>();
        foreach (var days in new[] { 8, 4, 6, 5, 7 })
        {
            var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
            await GoAsync(user.Client, product.OfferId);
            await AgeClicksAsync(user.Id, product.ProductId, days);
            products.Add(product);
        }

        // Clicked 4, 5, 6, 7 and 8 days ago: the three newest.
        var shown = await OpenAsync(user.Client);
        Assert.Equal([products[1].ProductId, products[3].ProductId, products[2].ProductId], shown.Select(prompt => prompt.GetProperty("product").GetProperty("id").GetGuid()));

        using var answered = await AnswerAsync(user.Client, shown[0].GetProperty("id").GetGuid(), "NotBought");
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        var next = await OpenAsync(user.Client);
        Assert.Equal([products[3].ProductId, products[2].ProductId, products[4].ProductId], next.Select(prompt => prompt.GetProperty("product").GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task AQuestionExpiresThirtyDaysAfterTheClick()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var old = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        await GoAsync(user.Client, old.OfferId);
        await AgeClicksAsync(user.Id, old.ProductId, 31);

        // A click that is already too old never becomes a question.
        Assert.Empty(await OpenAsync(user.Client));
        Assert.Equal(0, await factory.WithDbAsync(db => db.SizePrompts.CountAsync(row => row.UserId == user.Id, Ct)));

        var (_, promptId) = await ClickedProductAsync(user, 29);
        await factory.WithDbAsync(db => db.SizePrompts.Where(row => row.Id == promptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClickedAt, DateTimeOffset.UtcNow.AddDays(-31)), Ct));

        Assert.Empty(await OpenAsync(user.Client));
        using var late = await AnswerAsync(user.Client, promptId, "BoughtFits", "M");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("Profile.SizePromptExpired", (await late.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Equal(0, (await SizesAsync(user.Client)).GetArrayLength());
    }

    [Fact]
    public async Task ABagHasNoSizeToRemember()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var brand = Unique("Bag");
        var body = await user.Client.UpsertOkAsync($"{Slug(brand)}-store", [Item($"bag-{Guid.NewGuid():N}", 900m, brand, "Çanta", category: "aksesuar/canta")], Ct);
        var offerId = body.GetProperty("results")[0].GetProperty("offerId").GetGuid();
        var productId = body.GetProperty("results")[0].GetProperty("productId").GetGuid();

        await GoAsync(user.Client, offerId);
        await AgeClicksAsync(user.Id, productId, 5);

        Assert.Empty(await OpenAsync(user.Client));
    }

    [Fact]
    public async Task ManyAccessoryClicksNeverCrowdOutAnOlderClickOnSomethingWithASize()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var brand = Unique("Crowd");
        var body = await user.Client.UpsertOkAsync($"{Slug(brand)}-store", [Item($"crowd-{Guid.NewGuid():N}", 900m, brand, "Çanta", category: "aksesuar/canta")], Ct);
        var bag = body.GetProperty("results")[0];
        var shirt = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);

        // The shirt was clicked 20 days ago; since then, 250 clicks on a bag (newer, so a "newest 200" window is all bag clicks).
        await GoAsync(user.Client, shirt.OfferId);
        await AgeClicksAsync(user.Id, shirt.ProductId, 20);
        var sellerId = await factory.WithDbAsync(db => db.Offers.Where(offer => offer.Id == bag.GetProperty("offerId").GetGuid()).Select(offer => offer.SellerId).SingleAsync(Ct));
        await factory.WithDbAsync(async db =>
        {
            db.Clicks.AddRange(Enumerable.Range(0, 250).Select(index => new Kombin.Api.Persistence.Entities.Engagement.Click
            {
                UserId = user.Id,
                OfferId = bag.GetProperty("offerId").GetGuid(),
                ProductId = bag.GetProperty("productId").GetGuid(),
                SellerId = sellerId,
                Source = "product_detail",
                CorrelationId = Guid.NewGuid().ToString("N"),
                RedirectUrl = "https://shop.example.com/bag",
                ClickedAt = DateTimeOffset.UtcNow.AddDays(-4).AddMinutes(-index),
            }));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var open = await OpenAsync(user.Client);

        // The question about the shirt is there, and the bag never got one.
        Assert.Equal([shirt.ProductId], open.Select(prompt => prompt.GetProperty("product").GetProperty("id").GetGuid()));
        Assert.Equal(1, await factory.WithDbAsync(db => db.SizePrompts.CountAsync(row => row.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task AFittingSizeIsStoredAsConfirmedAndReplacesWhatTheUserTyped()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (product, promptId) = await ClickedProductAsync(user);
        using (var typed = await user.Client.PutAsJsonAsync("/api/v1/me/sizes", new { brandSlug = Slug(product.Brand), group = "Tops", size = "M" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        }

        // The size the user typed is offered back so the app can pre-fill it.
        var known = Assert.Single(await OpenAsync(user.Client)).GetProperty("knownSize");
        Assert.Equal(("M", "UserEntered"), (known.GetProperty("size").GetString(), known.GetProperty("source").GetString()));

        using var response = await AnswerAsync(user.Client, promptId, "BoughtFits", " l ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("BoughtFits", body.GetProperty("answer").GetString());
        var entry = body.GetProperty("sizeEntry");
        Assert.Equal(("L", "Confirmed", 1m, "Tops"), (entry.GetProperty("size").GetString(), entry.GetProperty("source").GetString(), entry.GetProperty("confidence").GetDecimal(), entry.GetProperty("group").GetString()));

        var stored = Assert.Single((await SizesAsync(user.Client)).EnumerateArray());
        Assert.Equal(("L", "Confirmed"), (stored.GetProperty("size").GetString(), stored.GetProperty("source").GetString()));
        Assert.Empty(await OpenAsync(user.Client));
    }

    [Fact]
    public async Task AFitCreatesTheEntryWhenThereWasNone()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (product, promptId) = await ClickedProductAsync(user);

        using var response = await AnswerAsync(user.Client, promptId, "BoughtFits", "xl");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = Assert.Single((await SizesAsync(user.Client)).EnumerateArray());
        Assert.Equal(("XL", "Confirmed", Slug(product.Brand)), (stored.GetProperty("size").GetString(), stored.GetProperty("source").GetString(), stored.GetProperty("brand").GetProperty("slug").GetString()));
    }

    [Fact]
    public async Task TooSmallOrTooLargeOnlyDoubtsTheStoredSizeAndNeverGuessesAnother()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (product, firstPrompt) = await ClickedProductAsync(user);
        using (var typed = await user.Client.PutAsJsonAsync("/api/v1/me/sizes", new { brandSlug = Slug(product.Brand), group = "Tops", size = "M" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        }

        // The stored size M did not fit: the size stays what the user typed, with less confidence.
        using var small = await AnswerAsync(user.Client, firstPrompt, "BoughtTooSmall");
        var doubted = (await small.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sizeEntry");
        Assert.Equal(("M", "UserEntered", 0.5m), (doubted.GetProperty("size").GetString(), doubted.GetProperty("source").GetString(), doubted.GetProperty("confidence").GetDecimal()));

        // Another product of the brand, bought in a different size than the stored one: says nothing about M.
        var secondProduct = await SecondProductOfBrandAsync(user, product);
        await AgeClicksAsync(user.Id, secondProduct, 5);
        using (var reset = await user.Client.PutAsJsonAsync("/api/v1/me/sizes", new { brandSlug = Slug(product.Brand), group = "Tops", size = "M" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        var secondPrompt = (await OpenAsync(user.Client)).Single(prompt => prompt.GetProperty("product").GetProperty("id").GetGuid() == secondProduct).GetProperty("id").GetGuid();
        using var larger = await AnswerAsync(user.Client, secondPrompt, "BoughtTooLarge", "L");
        var untouched = (await larger.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sizeEntry");
        Assert.Equal(("M", 1m), (untouched.GetProperty("size").GetString(), untouched.GetProperty("confidence").GetDecimal()));
    }

    [Fact]
    public async Task TooSmallWithNoStoredSizeStoresNothing()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (_, promptId) = await ClickedProductAsync(user);

        using var response = await AnswerAsync(user.Client, promptId, "BoughtTooSmall", "S");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sizeEntry").ValueKind);
        Assert.Equal(0, (await SizesAsync(user.Client)).GetArrayLength());
    }

    [Theory]
    [InlineData("NotBought")]
    [InlineData("NeverAsk")]
    public async Task NotBuyingAndNeverAskingLeaveTheSizeMapAloneAndTheProductIsNotAskedAgain(string answer)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (product, promptId) = await ClickedProductAsync(user);

        using var response = await AnswerAsync(user.Client, promptId, answer, "M");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await SizesAsync(user.Client)).GetArrayLength());
        Assert.Empty(await OpenAsync(user.Client));

        // Clicking the product again is no reason to ask again: one question per product.
        await GoAsync(user.Client, product.OfferId);
        await AgeClicksAsync(user.Id, product.ProductId, 5);
        Assert.Empty(await OpenAsync(user.Client));
        var row = await factory.WithDbAsync(db => db.SizePrompts.AsNoTracking().SingleAsync(candidate => candidate.Id == promptId, Ct));
        Assert.Equal(Enum.Parse<SizePromptAnswer>(answer), row.Answer);
        Assert.NotNull(row.AnsweredAt);
    }

    [Fact]
    public async Task TheSameAnswerTwiceIsFineAndAnotherOneIsRefused()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (_, promptId) = await ClickedProductAsync(user);

        using var first = await AnswerAsync(user.Client, promptId, "BoughtFits", "M");
        using var again = await AnswerAsync(user.Client, promptId, "BoughtFits", "m");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        using var changedMind = await AnswerAsync(user.Client, promptId, "BoughtTooSmall");
        Assert.Equal(HttpStatusCode.Conflict, changedMind.StatusCode);
        Assert.Equal("Profile.SizePromptAnswered", (await changedMind.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Equal(1m, Assert.Single((await SizesAsync(user.Client)).EnumerateArray()).GetProperty("confidence").GetDecimal());
    }

    [Fact]
    public async Task AnAnswerNeedsToBeKnownAndAFitNeedsItsSize()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (_, promptId) = await ClickedProductAsync(user);

        foreach (var (answer, size) in new (string, string?)[] { ("BoughtFits", null), ("BoughtFits", "  "), ("Perhaps", "M"), ("", null), ("BoughtTooLarge", new string('X', 21)) })
        {
            using var response = await AnswerAsync(user.Client, promptId, answer, size);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var unknown = await AnswerAsync(user.Client, Guid.NewGuid(), "NeverAsk");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Single(await OpenAsync(user.Client));
    }

    [Fact]
    public async Task TheSwitchStopsEveryQuestionAndTurningItOnBringsThemBack()
    {
        using var user = await factory.CreateUserAsync(Ct);

        Assert.True((await user.Client.GetJsonAsync("/api/v1/me/profile", Ct)).GetProperty("sizePromptsEnabled").GetBoolean());

        using (var off = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { sizePromptsEnabled = false }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sizePromptsEnabled").GetBoolean());
        }

        var product = await factory.SeedProductAsync(user.Client, 300m, cancellationToken: Ct);
        await GoAsync(user.Client, product.OfferId);
        await AgeClicksAsync(user.Id, product.ProductId, 5);

        // Off: no question, and none is even prepared.
        Assert.Empty(await OpenAsync(user.Client));
        Assert.Equal(0, await factory.WithDbAsync(db => db.SizePrompts.CountAsync(row => row.UserId == user.Id, Ct)));

        // Another field of the profile leaves the switch alone (a missing flag is "unchanged").
        using (var city = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { city = "Izmir" }, Ct))
        {
            Assert.False((await city.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sizePromptsEnabled").GetBoolean());
        }

        using (var on = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { sizePromptsEnabled = true }, Ct))
        {
            Assert.True((await on.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sizePromptsEnabled").GetBoolean());
        }

        Assert.Single(await OpenAsync(user.Client));
    }

    [Fact]
    public async Task TheRoutesNeedASignedInUser()
    {
        using var anonymous = factory.CreateAnonymousClient();

        using var list = await anonymous.GetAsync(Prompts, Ct);
        using var answer = await anonymous.PostAsJsonAsync($"{Prompts}/{Guid.NewGuid()}", new { answer = "NeverAsk" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
    }
}
