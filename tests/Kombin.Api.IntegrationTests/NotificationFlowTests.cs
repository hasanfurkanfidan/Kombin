using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Notification;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>The path from a lowered price to a push on the phone and a read entry in the feed, with the jobs invoked directly.</summary>
public sealed class NotificationFlowTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALoweredWatchedPriceBecomesAPushAndAFeedEntryThatCanBeRead()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var device = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(product.ProductId, "PriceDrop", 450m, cancellationToken: Ct);

        // The internal upsert lowers the price below the target: the alert fires and leaves a trigger.
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);

        var dispatched = await factory.DispatchAlertsAsync(user.Id, Ct);
        Assert.Equal(1, dispatched.Created);

        var pending = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Equal(NotificationStatus.Pending, pending.Status);
        Assert.Equal(NotificationKind.PriceDrop, pending.Kind);
        Assert.Equal("Takip ettiğin blazer 100 ₺ düştü", pending.Title);
        Assert.Equal($"{product.Brand} Blazer artık 400 ₺ — hedef fiyatının altında.", pending.Body);
        Assert.Equal($"pricedrop:{product.ProductId}", pending.DedupeKey);
        Assert.NotNull(pending.AlertTriggerId);
        Assert.True(pending.ScheduledFor <= DateTimeOffset.UtcNow);

        using (var data = JsonDocument.Parse(pending.Data))
        {
            Assert.Equal(product.ProductId, data.RootElement.GetProperty("productId").GetGuid());
            Assert.Equal(product.OfferId, data.RootElement.GetProperty("offerId").GetGuid());
            Assert.Equal($"kombin://product/{product.ProductId}", data.RootElement.GetProperty("deepLink").GetString());
        }

        // The trigger it came from is consumed in the same step.
        var consumed = await factory.WithDbAsync(db => db.AlertTriggers.AsNoTracking().Where(trigger => trigger.Id == pending.AlertTriggerId).Select(trigger => trigger.ConsumedAt).SingleAsync(Ct));
        Assert.NotNull(consumed);

        var run = await factory.SendPushesAsync(user.Id, Ct);
        var delivery = Assert.Single(run.Deliveries);
        Assert.Equal("ok", delivery.Status);

        var sent = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Equal(NotificationStatus.Sent, sent.Status);
        Assert.NotNull(sent.SentAt);
        Assert.Equal(FakeExpoPushClient.TicketIdFor(device), sent.ExpoTicketId);

        var message = Assert.Single(factory.Expo.Messages, candidate => candidate.To == device);
        Assert.Equal(pending.Title, message.Title);
        Assert.Equal(pending.Body, message.Body);
        Assert.Equal(product.ProductId, message.Data.GetProperty("productId").GetGuid());

        // What the app reports as notification_open: exactly this row and its kind.
        Assert.Equal(sent.Id, message.Data.GetProperty("notificationId").GetInt64());
        Assert.Equal("PriceDrop", message.Data.GetProperty("kind").GetString());
        Assert.Equal($"kombin://product/{product.ProductId}", message.Data.GetProperty("deepLink").GetString());

        var feed = await user.Client.FeedAsync(Ct);
        var entry = Assert.Single(feed.EnumerateArray());
        Assert.Equal("PriceDrop", entry.GetProperty("kind").GetString());
        Assert.Equal("Sent", entry.GetProperty("status").GetString());
        Assert.Equal(pending.Title, entry.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("readAt").ValueKind);
        Assert.Equal(product.ProductId, entry.GetProperty("data").GetProperty("productId").GetGuid());
        Assert.Equal(1, await user.Client.UnreadCountAsync(Ct));

        using var read = await user.Client.PostAsync($"/api/v1/me/notifications/{entry.GetProperty("id").GetInt64()}/read", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);

        Assert.Equal(0, await user.Client.UnreadCountAsync(Ct));
        Assert.NotEqual(JsonValueKind.Null, Assert.Single((await user.Client.FeedAsync(Ct)).EnumerateArray()).GetProperty("readAt").ValueKind);
    }

    [Fact]
    public async Task ReadingTwiceKeepsTheFirstReadTimeAndAnotherUsersNotificationIsNotFound()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var id = await factory.InsertNotificationAsync(user.Id, NotificationStatus.Sent, cancellationToken: Ct);

        using var first = await user.Client.PostAsync($"/api/v1/me/notifications/{id}/read", null, Ct);
        var readAt = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct)).ReadAt;
        using var second = await user.Client.PostAsync($"/api/v1/me/notifications/{id}/read", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.NotNull(readAt);
        Assert.Equal(readAt, Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct)).ReadAt);

        using var foreign = await other.Client.PostAsync($"/api/v1/me/notifications/{id}/read", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("Notification.NotFound", (await foreign.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var unknown = await user.Client.PostAsync("/api/v1/me/notifications/999999999/read", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ReadAllMarksEverythingInTheFeedAsRead()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertNotificationAsync(user.Id, NotificationStatus.Sent, cancellationToken: Ct);
        await factory.InsertNotificationAsync(user.Id, NotificationStatus.Suppressed, cancellationToken: Ct);
        await factory.InsertNotificationAsync(user.Id, NotificationStatus.Pending, cancellationToken: Ct);
        Assert.Equal(3, await user.Client.UnreadCountAsync(Ct));

        using var response = await user.Client.PostAsync("/api/v1/me/notifications/read-all", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await user.Client.UnreadCountAsync(Ct));
        Assert.All(await factory.NotificationsOfAsync(user.Id, Ct), notification => Assert.NotNull(notification.ReadAt));
    }

    [Fact]
    public async Task TheFeedIsNewestFirstPagedAndHidesFailedNotifications()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var first = await factory.InsertNotificationAsync(user.Id, NotificationStatus.Sent, cancellationToken: Ct);
        await factory.InsertNotificationAsync(user.Id, NotificationStatus.Failed, cancellationToken: Ct);
        var third = await factory.InsertNotificationAsync(user.Id, NotificationStatus.Suppressed, cancellationToken: Ct);
        var fourth = await factory.InsertNotificationAsync(user.Id, NotificationStatus.Pending, cancellationToken: Ct);

        var page = await user.Client.GetJsonAsync("/api/v1/me/notifications?page=1&pageSize=2", Ct);

        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal([fourth, third], page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));

        var rest = await user.Client.GetJsonAsync("/api/v1/me/notifications?page=2&pageSize=2", Ct);
        Assert.Equal([first], rest.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));

        // The failed one is neither listed, counted, nor readable.
        Assert.Equal(3, await user.Client.UnreadCountAsync(Ct));
        var failedId = (await factory.NotificationsOfAsync(user.Id, Ct)).Single(notification => notification.Status == NotificationStatus.Failed).Id;
        using var read = await user.Client.PostAsync($"/api/v1/me/notifications/{failedId}/read", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task NotificationEndpointsNeedAuthentication()
    {
        using var anonymous = factory.CreateAnonymousClient();

        foreach (var response in new[]
        {
            await anonymous.GetAsync("/api/v1/me/notifications", Ct),
            await anonymous.GetAsync("/api/v1/me/notifications/unread-count", Ct),
            await anonymous.PostAsync("/api/v1/me/notifications/1/read", null, Ct),
            await anonymous.PostAsync("/api/v1/me/notifications/read-all", null, Ct),
        })
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task ABackInStockAlertGetsItsOwnCopyAndKind()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        var product = await factory.SeedProductAsync(user.Client, 500m, stockStatus: "OutOfStock", sizes: ["M"], cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(product.ProductId, "BackInStock", cancellationToken: Ct);

        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(500m, "InStock", ["M", "L"])], Ct);
        await factory.DispatchAlertsAsync(user.Id, Ct);

        var notification = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Equal(NotificationKind.BackInStock, notification.Kind);
        Assert.Equal("Takip ettiğin blazer tekrar stokta", notification.Title);
        Assert.Equal($"{product.Brand} Blazer yeniden satışta — M, L bedenleri mevcut, fiyatı 500 ₺.", notification.Body);
        Assert.Equal($"backinstock:{product.ProductId}", notification.DedupeKey);
    }

    [Fact]
    public async Task TheInternalTestEndpointPushesASystemNotificationImmediately()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var device = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        using var internalClient = factory.CreateAnonymousClient();

        using var response = await internalClient.PostInternalAsync("/api/v1/internal/notifications/test", new { userId = user.Id, title = "Merhaba", body = "Deneme" }, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Sent", result.GetProperty("status").GetString());
        Assert.Equal(FakeExpoPushClient.TicketIdFor(device), result.GetProperty("ticketId").GetString());
        var delivery = Assert.Single(result.GetProperty("deliveries").EnumerateArray());
        Assert.Equal("ok", delivery.GetProperty("status").GetString());
        Assert.Equal(device, delivery.GetProperty("expoPushToken").GetString());

        var notification = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Equal(NotificationKind.System, notification.Kind);
        Assert.Equal("Merhaba", notification.Title);
        Assert.Equal("Deneme", notification.Body);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
    }

    [Fact]
    public async Task TheInternalTestEndpointReportsAnExpoErrorAsAFailedNotificationWithItsReason()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.RegisterDeviceAsync(NotificationClientExtensions.NewToken("toobig"), Ct);
        using var internalClient = factory.CreateAnonymousClient();

        using var response = await internalClient.PostInternalAsync("/api/v1/internal/notifications/test", new { userId = user.Id }, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Equal("MessageTooBig: Message too big.", result.GetProperty("failureReason").GetString());
        Assert.Equal("error", Assert.Single(result.GetProperty("deliveries").EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheInternalTestEndpointForAUserWithoutADeviceIsSuppressed()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var internalClient = factory.CreateAnonymousClient();

        using var response = await internalClient.PostInternalAsync("/api/v1/internal/notifications/test", new { userId = user.Id }, cancellationToken: Ct);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Suppressed", result.GetProperty("status").GetString());
        Assert.Equal("NoDevice", result.GetProperty("failureReason").GetString());
        Assert.Empty(result.GetProperty("deliveries").EnumerateArray());
    }

    [Fact]
    public async Task TheInternalTestEndpointNeedsTheKeyAndAKnownUser()
    {
        using var client = factory.CreateAnonymousClient();

        using var noKey = await client.PostInternalAsync("/api/v1/internal/notifications/test", new { userId = Guid.NewGuid() }, internalKey: null, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        using var unknown = await client.PostInternalAsync("/api/v1/internal/notifications/test", new { userId = Guid.NewGuid() }, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Notification.UserNotFound", (await unknown.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var empty = await client.PostInternalAsync("/api/v1/internal/notifications/test", new { userId = Guid.Empty }, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task OnlyTheUsersOwnNotificationsAreScopedToThem()
    {
        using var first = await factory.CreateUserAsync(Ct);
        using var second = await factory.CreateUserAsync(Ct);
        await factory.InsertNotificationAsync(first.Id, NotificationStatus.Sent, cancellationToken: Ct);

        Assert.Empty((await second.Client.FeedAsync(Ct)).EnumerateArray());
        Assert.Equal(0, await second.Client.UnreadCountAsync(Ct));
    }
}
