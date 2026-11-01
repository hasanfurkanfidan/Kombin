using Kombin.Api.Persistence.Entities.Notification;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.IntegrationTests;

/// <summary>The push sender against a real database and the token-driven <see cref="FakeExpoPushClient"/>.</summary>
public sealed class PushSenderTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NotificationEntity> SingleNotificationAsync(Guid userId) => Assert.Single(await factory.NotificationsOfAsync(userId, Ct));

    [Fact]
    public async Task ADeviceThatIsNotRegisteredIsDeactivatedAndTheNotificationStaysInTheFeed()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(NotificationClientExtensions.NewToken("gone"), Ct);
        await factory.InsertNotificationAsync(user.Id, cancellationToken: Ct);

        var run = await factory.SendPushesAsync(user.Id, Ct);

        var delivery = Assert.Single(run.Deliveries);
        Assert.Equal("error", delivery.Status);
        Assert.StartsWith("DeviceNotRegistered", delivery.Error, StringComparison.Ordinal);

        var device = Assert.Single(await factory.DevicesWithTokenAsync(token, Ct));
        Assert.False(device.IsActive);
        Assert.Equal("DeviceNotRegistered", device.DeactivationReason);

        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Suppressed, notification.Status);
        Assert.Equal("DeviceNotRegistered", notification.FailureReason);
        Assert.Equal("Suppressed", Assert.Single((await user.Client.FeedAsync(Ct)).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnyOtherTicketErrorFailsTheNotificationWithItsReasonAndKeepsTheDevice()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(NotificationClientExtensions.NewToken("toobig"), Ct);
        await factory.InsertNotificationAsync(user.Id, cancellationToken: Ct);

        await factory.SendPushesAsync(user.Id, Ct);

        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Equal("MessageTooBig: Message too big.", notification.FailureReason);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(token, Ct)).IsActive);
        Assert.Empty((await user.Client.FeedAsync(Ct)).EnumerateArray());
    }

    [Fact]
    public async Task AUserWithTwoDevicesGetsTwoMessagesAndOneRow()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var phone = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        var tablet = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        await factory.InsertNotificationAsync(user.Id, cancellationToken: Ct);

        var run = await factory.SendPushesAsync(user.Id, Ct);

        Assert.Equal(2, run.Deliveries.Count);
        Assert.Contains(factory.Expo.Messages, message => message.To == phone);
        Assert.Contains(factory.Expo.Messages, message => message.To == tablet);

        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Equal(
            new[] { FakeExpoPushClient.TicketIdFor(phone), FakeExpoPushClient.TicketIdFor(tablet) }.Order(),
            notification.ExpoTicketId!.Split(',').Order());
    }

    [Fact]
    public async Task OneDeviceAcceptingThePushIsEnoughForSentWhileTheDeadOneIsDeactivated()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var good = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        var gone = await user.Client.RegisterDeviceAsync(NotificationClientExtensions.NewToken("gone"), Ct);
        await factory.InsertNotificationAsync(user.Id, cancellationToken: Ct);

        await factory.SendPushesAsync(user.Id, Ct);

        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Equal(FakeExpoPushClient.TicketIdFor(good), notification.ExpoTicketId);
        Assert.Null(notification.FailureReason);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(good, Ct)).IsActive);
        Assert.False(Assert.Single(await factory.DevicesWithTokenAsync(gone, Ct)).IsActive);
    }

    [Fact]
    public async Task AFailedCallLeavesTheNotificationPendingAndCountsTheAttemptUntilTheFifthFailsIt()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.RegisterDeviceAsync(NotificationClientExtensions.NewToken("http-fail"), Ct);
        await factory.InsertNotificationAsync(user.Id, cancellationToken: Ct);

        for (var attempt = 1; attempt < NotificationEntity.MaxAttempts; attempt++)
        {
            var run = await factory.SendPushesAsync(user.Id, Ct);

            Assert.Equal("unreachable", Assert.Single(run.Deliveries).Status);
            var pending = await SingleNotificationAsync(user.Id);
            Assert.Equal(NotificationStatus.Pending, pending.Status);
            Assert.Equal(attempt, pending.AttemptCount);
            Assert.StartsWith("ExpoUnavailable: HTTP 503", pending.FailureReason, StringComparison.Ordinal);
            Assert.Equal(1, await user.Client.UnreadCountAsync(Ct));
        }

        await factory.SendPushesAsync(user.Id, Ct);

        var failed = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Failed, failed.Status);
        Assert.Equal(NotificationEntity.MaxAttempts, failed.AttemptCount);
        Assert.Contains("ExpoUnavailable", failed.FailureReason, StringComparison.Ordinal);

        // Failed for good: later runs do not pick it up again, and the feed hides it.
        Assert.Equal(0, (await factory.SendPushesAsync(user.Id, Ct)).Notifications);
        Assert.Empty((await user.Client.FeedAsync(Ct)).EnumerateArray());
    }

    [Fact]
    public async Task MessagesGoOutInBatchesOfAHundredAndEveryNotificationIsSent()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);

        await factory.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;

            for (var index = 0; index < 230; index++)
            {
                db.Notifications.Add(new NotificationEntity
                {
                    UserId = user.Id,
                    Kind = NotificationKind.System,
                    Title = $"Bildirim {index}",
                    Body = "Test",
                    DedupeKey = $"batch:{index}",
                    CreatedAt = now,
                    ScheduledFor = now.AddSeconds(-5),
                    Status = NotificationStatus.Pending,
                });
            }

            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var run = await factory.SendPushesAsync(user.Id, Ct);

        Assert.Equal(230, run.Notifications);
        Assert.Equal(230, run.Deliveries.Count);
        Assert.Equal([100, 100, 30], factory.Expo.Batches.Where(batch => batch.Any(message => message.To == token)).Select(batch => batch.Count));
        Assert.All(await factory.NotificationsOfAsync(user.Id, Ct), notification => Assert.Equal(NotificationStatus.Sent, notification.Status));
    }

    [Fact]
    public async Task ANotificationScheduledForLaterAndOneAlreadySentAreLeftAlone()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        await factory.InsertNotificationAsync(user.Id, scheduledFor: DateTimeOffset.UtcNow.AddHours(2), cancellationToken: Ct);
        await factory.InsertNotificationAsync(user.Id, NotificationStatus.Sent, sentAt: DateTimeOffset.UtcNow.AddMinutes(-1), ticketIds: "ticket-x", cancellationToken: Ct);

        var run = await factory.SendPushesAsync(user.Id, Ct);

        Assert.Equal(0, run.Notifications);
        Assert.DoesNotContain(factory.Expo.Messages, message => message.To == token);
    }

    [Fact]
    public async Task AUserWhoseDevicesAreAllGoneByTheTimeOfSendingIsSuppressed()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        await factory.InsertNotificationAsync(user.Id, cancellationToken: Ct);
        (await user.Client.DeleteDeviceAsync(token, Ct)).Dispose();

        await factory.SendPushesAsync(user.Id, Ct);

        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Suppressed, notification.Status);
        Assert.Equal("NoDevice", notification.FailureReason);
        Assert.DoesNotContain(factory.Expo.Messages, message => message.To == token);
    }

    [Fact]
    public async Task ANotificationIsOnlyEverSentOnce()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        await factory.InsertNotificationAsync(user.Id, cancellationToken: Ct);

        await factory.SendPushesAsync(user.Id, Ct);
        var second = await factory.SendPushesAsync(user.Id, Ct);

        Assert.Equal(0, second.Notifications);
        Assert.Single(factory.Expo.Messages, message => message.To == token);
    }
}
