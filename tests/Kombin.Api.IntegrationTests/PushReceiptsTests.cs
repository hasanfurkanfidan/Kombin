using Kombin.Api.Persistence.Entities.Notification;
using Microsoft.EntityFrameworkCore;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.IntegrationTests;

public sealed class PushReceiptsTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NotificationEntity> SingleNotificationAsync(Guid userId) => Assert.Single(await factory.NotificationsOfAsync(userId, Ct));

    /// <summary>A user with one device whose push was accepted <paramref name="minutesAgo"/> minutes ago; the device is older than that.</summary>
    private async Task<(TestUser User, string Token)> ArrangeSentPushAsync(string flavor, int minutesAgo = 20)
    {
        var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(NotificationClientExtensions.NewToken(flavor), Ct);
        await BackdateDevicesAsync(user.Id);
        await factory.InsertNotificationAsync(
            user.Id,
            NotificationStatus.Sent,
            sentAt: DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            ticketIds: FakeExpoPushClient.TicketIdFor(token),
            cancellationToken: Ct);

        return (user, token);
    }

    private Task<int> BackdateDevicesAsync(Guid userId) =>
        factory.WithDbAsync(db => db.Devices.Where(device => device.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(device => device.CreatedAt, DateTimeOffset.UtcNow.AddDays(-1)), Ct));

    [Fact]
    public async Task AnOkReceiptMarksTheNotificationAsChecked()
    {
        var (user, token) = await ArrangeSentPushAsync("fine");
        using var _ = user;

        var checkedCount = await factory.CheckReceiptsAsync(user.Id, Ct);

        Assert.Equal(1, checkedCount);
        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.NotNull(notification.ReceiptCheckedAt);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(token, Ct)).IsActive);

        // Each notification is checked once.
        Assert.Equal(0, await factory.CheckReceiptsAsync(user.Id, Ct));
    }

    [Fact]
    public async Task ADeviceNotRegisteredReceiptDeactivatesTheDevice()
    {
        var (user, token) = await ArrangeSentPushAsync("receipt-gone");
        using var _ = user;

        await factory.CheckReceiptsAsync(user.Id, Ct);

        var device = Assert.Single(await factory.DevicesWithTokenAsync(token, Ct));
        Assert.False(device.IsActive);
        Assert.Equal("DeviceNotRegistered", device.DeactivationReason);

        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Suppressed, notification.Status);
        Assert.Equal("DeviceNotRegistered", notification.FailureReason);
        Assert.NotNull(notification.ReceiptCheckedAt);
    }

    [Fact]
    public async Task AnyOtherReceiptErrorFailsTheNotificationWithItsReason()
    {
        var (user, token) = await ArrangeSentPushAsync("receipt-err");
        using var _ = user;

        await factory.CheckReceiptsAsync(user.Id, Ct);

        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Equal("MessageRateExceeded: Too many messages.", notification.FailureReason);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(token, Ct)).IsActive);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25 * 60)]
    public async Task OnlyPushesSentBetweenFifteenMinutesAndADayAgoAreChecked(int minutesAgo)
    {
        var (user, _) = await ArrangeSentPushAsync("fine", minutesAgo);
        using var owner = user;

        Assert.Equal(0, await factory.CheckReceiptsAsync(user.Id, Ct));
        Assert.Null((await SingleNotificationAsync(user.Id)).ReceiptCheckedAt);
    }

    [Fact]
    public async Task AReceiptThatIsNotReadyYetIsAskedForAgainLater()
    {
        var (user, _) = await ArrangeSentPushAsync("not-ready");
        using var owner = user;

        Assert.Equal(0, await factory.CheckReceiptsAsync(user.Id, Ct));

        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Null(notification.ReceiptCheckedAt);
    }

    [Fact]
    public async Task ADeadDeviceIsLeftAloneWhenTheReceiptCannotSayWhichOneItWas()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var phone = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        var tablet = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        await BackdateDevicesAsync(user.Id);
        var tickets = $"{FakeExpoPushClient.TicketIdFor(phone)},{FakeExpoPushClient.TicketIdFor(NotificationClientExtensions.NewToken("receipt-gone"))}";
        await factory.InsertNotificationAsync(user.Id, NotificationStatus.Sent, sentAt: DateTimeOffset.UtcNow.AddMinutes(-20), ticketIds: tickets, cancellationToken: Ct);

        await factory.CheckReceiptsAsync(user.Id, Ct);

        // One of the two pushes was delivered, so the notification stands; with two candidate devices nothing is deactivated on a guess.
        var notification = await SingleNotificationAsync(user.Id);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.NotNull(notification.ReceiptCheckedAt);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(phone, Ct)).IsActive);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(tablet, Ct)).IsActive);
    }
}
