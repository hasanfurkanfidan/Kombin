using System.Globalization;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Notification.Jobs;
using Kombin.Api.Persistence.Entities.Notification;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>The alert dispatcher against a real database; the rules themselves are covered without one in <c>DispatchRulesTests</c>.</summary>
public sealed class AlertDispatcherTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestUser User, SeededProduct Product)> ArrangeWatchedPriceDropAsync(bool withDevice = true)
    {
        var user = await factory.CreateUserAsync(Ct);

        if (withDevice)
        {
            await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        }

        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(product.ProductId, "PriceDrop", 450m, cancellationToken: Ct);

        return (user, product);
    }

    private Task<List<DateTimeOffset?>> TriggerConsumptionAsync(Guid userId) =>
        factory.WithDbAsync(db => db.AlertTriggers.AsNoTracking().Where(trigger => trigger.UserId == userId).OrderBy(trigger => trigger.Id).Select(trigger => trigger.ConsumedAt).ToListAsync(Ct));

    [Fact]
    public async Task APreferenceThatIsOffSkipsTheNotificationButConsumesTheTrigger()
    {
        var (user, product) = await ArrangeWatchedPriceDropAsync();
        using var _ = user;
        (await user.Client.PutPreferencesAsync(new { priceAlerts = false }, Ct)).Dispose();
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);

        var summary = await factory.DispatchAlertsAsync(user.Id, Ct);

        Assert.Equal(new DispatchSummary(0, 1), summary);
        Assert.Empty(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.All(await TriggerConsumptionAsync(user.Id), consumedAt => Assert.NotNull(consumedAt));
    }

    [Fact]
    public async Task TheStockPreferenceOnlyGovernsBackInStockAlerts()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        (await user.Client.PutPreferencesAsync(new { stockAlerts = false }, Ct)).Dispose();
        var product = await factory.SeedProductAsync(user.Client, 500m, cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(product.ProductId, "PriceDrop", 450m, cancellationToken: Ct);
        var stockProduct = await factory.SeedProductAsync(user.Client, 500m, stockStatus: "OutOfStock", cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(stockProduct.ProductId, "BackInStock", cancellationToken: Ct);

        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);
        await user.Client.UpsertOkAsync(stockProduct.SellerSlug, [stockProduct.Item(500m)], Ct);
        var summary = await factory.DispatchAlertsAsync(user.Id, Ct);

        Assert.Equal(new DispatchSummary(1, 1), summary);
        Assert.Equal(NotificationKind.PriceDrop, Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct)).Kind);
    }

    [Fact]
    public async Task TriggersForTheSameProductWithinADayCollapseIntoOneNotification()
    {
        var (user, product) = await ArrangeWatchedPriceDropAsync();
        using var _ = user;

        // Down, up, down again before the dispatcher runs: two triggers.
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(500m)], Ct);
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(420m)], Ct);

        var first = await factory.DispatchAlertsAsync(user.Id, Ct);
        Assert.Equal(new DispatchSummary(1, 1), first);

        // And once more the next minute: still the same day.
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(500m)], Ct);
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(410m)], Ct);
        var second = await factory.DispatchAlertsAsync(user.Id, Ct);

        Assert.Equal(new DispatchSummary(0, 1), second);
        Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.All(await TriggerConsumptionAsync(user.Id), consumedAt => Assert.NotNull(consumedAt));
    }

    [Fact]
    public async Task ANotificationOlderThanADayDoesNotBlockTheNext()
    {
        var (user, product) = await ArrangeWatchedPriceDropAsync();
        using var _ = user;
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);
        await factory.DispatchAlertsAsync(user.Id, Ct);
        await factory.WithDbAsync(db => db.Notifications.Where(notification => notification.UserId == user.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.CreatedAt, DateTimeOffset.UtcNow.AddHours(-25)), Ct));

        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(500m)], Ct);
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(380m)], Ct);
        var summary = await factory.DispatchAlertsAsync(user.Id, Ct);

        Assert.Equal(new DispatchSummary(1, 0), summary);
        Assert.Equal(2, (await factory.NotificationsOfAsync(user.Id, Ct)).Count);
    }

    [Fact]
    public async Task AUserWithoutADeviceGetsASuppressedNotificationThatShowsInTheFeed()
    {
        var (user, product) = await ArrangeWatchedPriceDropAsync(withDevice: false);
        using var _ = user;
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);

        var summary = await factory.DispatchAlertsAsync(user.Id, Ct);

        Assert.Equal(new DispatchSummary(1, 0), summary);
        var notification = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Equal(NotificationStatus.Suppressed, notification.Status);
        Assert.Equal("NoDevice", notification.FailureReason);

        Assert.Equal("Suppressed", Assert.Single((await user.Client.FeedAsync(Ct)).EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(1, await user.Client.UnreadCountAsync(Ct));

        // Nothing for the sender to do, and no push goes anywhere.
        Assert.Empty((await factory.SendPushesAsync(user.Id, Ct)).Deliveries);
        Assert.Equal(NotificationStatus.Suppressed, Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct)).Status);
    }

    [Fact]
    public async Task QuietHoursHoldTheNotificationUntilTheyEnd()
    {
        var (user, product) = await ArrangeWatchedPriceDropAsync();
        using var _ = user;
        var device = Assert.Single(await factory.WithDbAsync(db => db.Devices.AsNoTracking().Where(candidate => candidate.UserId == user.Id).ToListAsync(Ct)));

        // A window around the current Istanbul time, which also works when it wraps past midnight.
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, QuietHours.Zone);
        (await user.Client.PutPreferencesAsync(new
        {
            quietHoursStart = local.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture),
            quietHoursEnd = local.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture),
        }, Ct)).Dispose();
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);

        await factory.DispatchAlertsAsync(user.Id, Ct);

        var notification = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.InRange(notification.ScheduledFor, DateTimeOffset.UtcNow.AddMinutes(58), DateTimeOffset.UtcNow.AddMinutes(61));

        // Not due yet: the sender leaves it alone.
        Assert.Empty((await factory.SendPushesAsync(user.Id, Ct)).Deliveries);
        Assert.DoesNotContain(factory.Expo.Messages, message => message.To == device.ExpoPushToken);
        Assert.Equal(NotificationStatus.Pending, Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct)).Status);
    }

    [Fact]
    public async Task RunningTheDispatcherAgainFindsNothingToDo()
    {
        var (user, product) = await ArrangeWatchedPriceDropAsync();
        using var _ = user;
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);

        await factory.DispatchAlertsAsync(user.Id, Ct);
        var again = await factory.DispatchAlertsAsync(user.Id, Ct);

        Assert.Equal(default, again);
        Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
    }

    [Fact]
    public async Task TheDispatcherOnlyTouchesTheRequestedUser()
    {
        var (user, product) = await ArrangeWatchedPriceDropAsync();
        using var _ = user;
        using var bystander = await factory.CreateUserAsync(Ct);
        await bystander.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);
        await user.Client.UpsertOkAsync(product.SellerSlug, [product.Item(400m)], Ct);

        await factory.DispatchAlertsAsync(user.Id, Ct);

        Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Empty(await factory.NotificationsOfAsync(bystander.Id, Ct));
        Assert.All(await TriggerConsumptionAsync(bystander.Id), consumedAt => Assert.Null(consumedAt));
    }
}
