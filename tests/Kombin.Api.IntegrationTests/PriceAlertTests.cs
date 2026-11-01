using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class PriceAlertTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APriceDropAlertIsCreatedWithItsProductSummary()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);

        using var response = await user.Client.CreateAlertAsync(product.ProductId, "PriceDrop", 450m, null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var alert = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("PriceDrop", alert.GetProperty("kind").GetString());
        Assert.Equal(450m, alert.GetProperty("targetPrice").GetDecimal());
        Assert.True(alert.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, alert.GetProperty("triggerCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, alert.GetProperty("lastTriggeredAt").ValueKind);

        var summary = alert.GetProperty("product");
        Assert.Equal(product.ProductId, summary.GetProperty("id").GetGuid());
        Assert.Equal("Blazer", summary.GetProperty("name").GetString());
        Assert.Equal(product.Brand, summary.GetProperty("brandName").GetString());
        Assert.Equal(500m, summary.GetProperty("lowestPrice").GetDecimal());
        Assert.StartsWith("https://img.example.com/", summary.GetProperty("primaryImageUrl").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APriceDropTargetMustBeBelowTheCurrentLowestEffectivePrice()
    {
        using var user = await factory.CreateUserAsync(Ct);

        // 400 + 49.90 shipping: effective 449.90.
        var product = await factory.SeedProductAsync(user.Client, 400m, shippingCost: 49.90m, cancellationToken: Ct);

        foreach (var target in new[] { 449.90m, 450m, 1000m })
        {
            using var rejected = await user.Client.CreateAlertAsync(product.ProductId, "PriceDrop", target, null, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var problem = await rejected.ReadProblemAsync(Ct);
            Assert.Equal("Engagement.TargetNotBelowCurrent", problem.GetProperty("errorCode").GetString());
        }

        using var accepted = await user.Client.CreateAlertAsync(product.ProductId, "PriceDrop", 449.89m, null, Ct);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task AnActiveAlertOfTheSameKindForTheSameProductIsAConflict()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);

        await user.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);

        using var duplicate = await user.Client.CreateAlertAsync(product.ProductId, "AnyChange", null, null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await duplicate.ReadProblemAsync(Ct);
        Assert.Equal("Engagement.AlertExists", problem.GetProperty("errorCode").GetString());

        // Another kind, and another user, are different alerts.
        await user.Client.CreateAlertOkAsync(product.ProductId, "BackInStock", cancellationToken: Ct);
        await other.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);
    }

    [Fact]
    public async Task SwitchingAnAlertOffMakesRoomForANewOneAndSwitchingItBackOnRespectsTheRule()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        var first = await user.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);

        using var off = await user.Client.PatchJsonAsync($"/api/v1/me/alerts/{first}", new { isActive = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var second = await user.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);

        using var back = await user.Client.PatchJsonAsync($"/api/v1/me/alerts/{first}", new { isActive = true }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal("Engagement.AlertExists", (await back.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        var active = await user.Client.GetJsonAsync("/api/v1/me/alerts", Ct);
        Assert.Equal([second], active.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));

        var inactive = await user.Client.GetJsonAsync("/api/v1/me/alerts?active=false", Ct);
        Assert.Equal([first], inactive.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task InvalidAlertsAreRejected()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);

        using var noTarget = await user.Client.CreateAlertAsync(product.ProductId, "PriceDrop", null, null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noTarget.StatusCode);
        Assert.True((await noTarget.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("targetPrice", out _));

        using var targetOnWrongKind = await user.Client.CreateAlertAsync(product.ProductId, "AnyChange", 100m, null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, targetOnWrongKind.StatusCode);

        using var badKind = await user.Client.CreateAlertAsync(product.ProductId, "Cheaper", null, null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.True((await badKind.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("kind", out _));

        using var unknownProduct = await user.Client.CreateAlertAsync(Guid.NewGuid(), "AnyChange", null, null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknownProduct.StatusCode);
        Assert.Equal("Catalog.ProductNotFound", (await unknownProduct.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AnAlertCanOnlyBeSeenChangedAndDeletedByItsOwner()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(owner.Client, 500m, cancellationToken: Ct);
        var alertId = await owner.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);

        using var patched = await stranger.Client.PatchJsonAsync($"/api/v1/me/alerts/{alertId}", new { isActive = false }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, patched.StatusCode);
        Assert.Equal("Engagement.AlertNotFound", (await patched.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var deleted = await stranger.Client.DeleteAsync($"/api/v1/me/alerts/{alertId}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

        Assert.Equal(0, (await stranger.Client.GetJsonAsync("/api/v1/me/alerts", Ct)).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await owner.Client.GetJsonAsync("/api/v1/me/alerts", Ct)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AnAlertCanBeEditedWithPatch()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var priceProduct = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        var stockProduct = await factory.SeedProductAsync(user.Client, 500m, "OutOfStock", cancellationToken: Ct);
        var priceAlert = await user.Client.CreateAlertOkAsync(priceProduct.ProductId, "PriceDrop", 450m, cancellationToken: Ct);
        var stockAlert = await user.Client.CreateAlertOkAsync(stockProduct.ProductId, "BackInStock", sizes: ["S"], cancellationToken: Ct);

        using var newTarget = await user.Client.PatchJsonAsync($"/api/v1/me/alerts/{priceAlert}", new { targetPrice = 400m }, Ct);
        Assert.Equal(HttpStatusCode.OK, newTarget.StatusCode);
        Assert.Equal(400m, (await newTarget.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("targetPrice").GetDecimal());

        using var notBelow = await user.Client.PatchJsonAsync($"/api/v1/me/alerts/{priceAlert}", new { targetPrice = 500m }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notBelow.StatusCode);
        Assert.Equal("Engagement.TargetNotBelowCurrent", (await notBelow.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        string[] wanted = ["M", "L"];
        using var newSizes = await user.Client.PatchJsonAsync($"/api/v1/me/alerts/{stockAlert}", new { sizes = wanted }, Ct);
        Assert.Equal(HttpStatusCode.OK, newSizes.StatusCode);
        Assert.Equal(["M", "L"], (await newSizes.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sizes").EnumerateArray().Select(size => size.GetString()));

        using var targetOnStockAlert = await user.Client.PatchJsonAsync($"/api/v1/me/alerts/{stockAlert}", new { targetPrice = 10m }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, targetOnStockAlert.StatusCode);

        using var empty = await user.Client.PatchJsonAsync($"/api/v1/me/alerts/{stockAlert}", new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task APriceDropAlertFiresExactlyOnceAsThePriceGoesAboveThenBelowThenStaysBelow()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 200m, cancellationToken: Ct);
        var alertId = await user.Client.CreateAlertOkAsync(product.ProductId, "PriceDrop", 150m, cancellationToken: Ct);

        // 1. Still above the target.
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(180m)], Ct);
        Assert.Equal(0, await TriggerCountAsync(user));

        // 2. Crosses the target.
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(140m)], Ct);
        Assert.Equal(1, await TriggerCountAsync(user));

        // 3. Still below the target, and an identical re-observation.
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(130m)], Ct);
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(130m)], Ct);
        Assert.Equal(1, await TriggerCountAsync(user));

        var trigger = (await user.Client.GetJsonAsync("/api/v1/me/alerts/triggers", Ct)).GetProperty("items")[0];
        Assert.Equal(alertId, trigger.GetProperty("alertId").GetGuid());
        Assert.Equal("PriceDrop", trigger.GetProperty("kind").GetString());
        Assert.Equal(product.OfferId, trigger.GetProperty("offerId").GetGuid());
        Assert.Equal(180m, trigger.GetProperty("priceBefore").GetDecimal());
        Assert.Equal(140m, trigger.GetProperty("priceAfter").GetDecimal());
        Assert.Equal(product.ProductId, trigger.GetProperty("product").GetProperty("id").GetGuid());
        Assert.Equal(product.SellerSlug, trigger.GetProperty("payload").GetProperty("sellerSlug").GetString());
        Assert.Equal(JsonValueKind.Null, trigger.GetProperty("consumedAt").ValueKind);

        // The alert stays active and remembers what happened.
        var alert = (await user.Client.GetJsonAsync("/api/v1/me/alerts", Ct)).GetProperty("items")[0];
        Assert.True(alert.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, alert.GetProperty("triggerCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, alert.GetProperty("lastTriggeredAt").ValueKind);
        Assert.Equal(140m, alert.GetProperty("lastPriceAfter").GetDecimal());

        // Back above and down again is a second crossing.
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(160m)], Ct);
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(120m)], Ct);
        Assert.Equal(2, await TriggerCountAsync(user));
    }

    [Fact]
    public async Task BackInStockFiresOnlyForUsersWhoseSizeFilterMatches()
    {
        using var wantsM = await factory.CreateUserAsync(Ct);
        using var wantsXl = await factory.CreateUserAsync(Ct);
        using var anySize = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(wantsM.Client, 300m, "OutOfStock", sizes: [], cancellationToken: Ct);

        await wantsM.Client.CreateAlertOkAsync(product.ProductId, "BackInStock", sizes: ["M"], cancellationToken: Ct);
        await wantsXl.Client.CreateAlertOkAsync(product.ProductId, "BackInStock", sizes: ["XL"], cancellationToken: Ct);
        await anySize.Client.CreateAlertOkAsync(product.ProductId, "BackInStock", cancellationToken: Ct);

        // Still out of stock: nothing.
        await wantsM.Client.UpsertOkAsync(product.SellerSlug, [product.Item(300m, "OutOfStock", sizes: [])], Ct);
        Assert.Equal(0, await TriggerCountAsync(wantsM));

        // Back with S and M only.
        await wantsM.Client.UpsertOkAsync(product.SellerSlug, [product.Item(300m, "InStock", sizes: ["S", "M"])], Ct);

        Assert.Equal(1, await TriggerCountAsync(wantsM));
        Assert.Equal(0, await TriggerCountAsync(wantsXl));
        Assert.Equal(1, await TriggerCountAsync(anySize));

        var trigger = (await wantsM.Client.GetJsonAsync("/api/v1/me/alerts/triggers", Ct)).GetProperty("items")[0];
        Assert.Equal("BackInStock", trigger.GetProperty("kind").GetString());
        Assert.Equal(["M"], trigger.GetProperty("payload").GetProperty("sizes").EnumerateArray().Select(size => size.GetString()));
        Assert.Equal("OutOfStock", trigger.GetProperty("payload").GetProperty("stockBefore").GetString());
        Assert.Equal("InStock", trigger.GetProperty("payload").GetProperty("stockAfter").GetString());

        // Staying in stock fires nothing more.
        await wantsM.Client.UpsertOkAsync(product.SellerSlug, [product.Item(300m, "InStock", sizes: ["S", "M"])], Ct);
        Assert.Equal(1, await TriggerCountAsync(wantsM));
    }

    [Fact]
    public async Task AnyChangeFiresOnAPriceMoveOfOnePercentAndNotOnLess()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 1000m, cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);

        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(1005m)], Ct);
        Assert.Equal(0, await TriggerCountAsync(user));

        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(1020m)], Ct);
        Assert.Equal(1, await TriggerCountAsync(user));
    }

    [Fact]
    public async Task DeletingAnAlertKeepsItsTriggersInTheFeed()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        var alertId = await user.Client.CreateAlertOkAsync(product.ProductId, "PriceDrop", 400m, cancellationToken: Ct);
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(390m)], Ct);
        Assert.Equal(1, await TriggerCountAsync(user));

        using var deleted = await user.Client.DeleteAsync($"/api/v1/me/alerts/{alertId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(0, (await user.Client.GetJsonAsync("/api/v1/me/alerts?active=true", Ct)).GetProperty("items").GetArrayLength());
        var feed = await user.Client.GetJsonAsync("/api/v1/me/alerts/triggers", Ct);
        Assert.Equal(1, feed.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("items")[0].GetProperty("alertId").ValueKind);
    }

    [Fact]
    public async Task AnInactiveAlertDoesNotFire()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        var alertId = await user.Client.CreateAlertOkAsync(product.ProductId, "PriceDrop", 400m, cancellationToken: Ct);
        using var off = await user.Client.PatchJsonAsync($"/api/v1/me/alerts/{alertId}", new { isActive = false }, Ct);

        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(390m)], Ct);

        Assert.Equal(0, await TriggerCountAsync(user));
    }

    [Fact]
    public async Task TheTriggerFeedIsPagedNewestFirstAndPrivate()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);

        foreach (var price in new[] { 600m, 700m, 800m })
        {
            await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(price)], Ct);
        }

        var page = await user.Client.GetJsonAsync("/api/v1/me/alerts/triggers?page=1&pageSize=2", Ct);
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal([800m, 700m], page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("priceAfter").GetDecimal()));

        Assert.Equal(0, await TriggerCountAsync(other));
    }

    private static async Task<int> TriggerCountAsync(TestUser user)
    {
        var feed = await user.Client.GetJsonAsync("/api/v1/me/alerts/triggers", Ct);
        return feed.GetProperty("totalCount").GetInt32();
    }
}
