using Kombin.Api.Features.Notification;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Tests;

public class DispatchRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static NotificationPreference Preferences(
        bool priceAlerts = true, bool stockAlerts = true, TimeOnly? quietStart = null, TimeOnly? quietEnd = null) =>
        new() { PriceAlerts = priceAlerts, StockAlerts = stockAlerts, QuietHoursStart = quietStart, QuietHoursEnd = quietEnd };

    private static DispatchDecision Decide(
        NotificationKind kind = NotificationKind.PriceDrop,
        NotificationPreference? preference = null,
        bool hasDevice = true,
        bool duplicate = false) =>
        DispatchRules.Decide(kind, preference, hasDevice, duplicate, Now, QuietHours.Zone);

    [Fact]
    public void ADeviceAndNoObstaclesMakeAPendingNotificationDueNow()
    {
        var decision = Decide();

        Assert.True(decision.Create);
        Assert.Equal(NotificationStatus.Pending, decision.Status);
        Assert.Null(decision.FailureReason);
        Assert.Equal(Now, decision.ScheduledFor);
    }

    [Fact]
    public void AUserWhoNeverSetPreferencesGetsEverything()
    {
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            Assert.True(Decide(kind, preference: null).Create);
        }
    }

    [Theory]
    [InlineData("PriceDrop")]
    [InlineData("AnyChange")]
    public void SwitchingPriceAlertsOffSkipsPriceDropAndAnyChange(string kindName)
    {
        var decision = Decide(Enum.Parse<NotificationKind>(kindName), Preferences(priceAlerts: false));

        Assert.False(decision.Create);
        Assert.Equal(DispatchSkip.PreferenceOff, decision.Skip);
    }

    [Fact]
    public void SwitchingStockAlertsOffSkipsBackInStockOnly()
    {
        var preference = Preferences(stockAlerts: false);

        Assert.Equal(DispatchSkip.PreferenceOff, Decide(NotificationKind.BackInStock, preference).Skip);
        Assert.True(Decide(NotificationKind.PriceDrop, preference).Create);
        Assert.True(Decide(NotificationKind.AnyChange, preference).Create);
    }

    [Fact]
    public void ThePreferenceCheckComesBeforeTheOthers()
    {
        var decision = Decide(preference: Preferences(priceAlerts: false), hasDevice: false, duplicate: true);

        Assert.Equal(DispatchSkip.PreferenceOff, decision.Skip);
    }

    [Fact]
    public void ARepeatWithinTheWindowIsSkipped()
    {
        var decision = Decide(duplicate: true);

        Assert.False(decision.Create);
        Assert.Equal(DispatchSkip.Duplicate, decision.Skip);
    }

    [Fact]
    public void TheDedupeWindowIsTwentyFourHours() =>
        Assert.Equal(TimeSpan.FromHours(24), DispatchRules.DedupeWindow);

    [Fact]
    public void WithoutADeviceTheNotificationIsKeptAsSuppressedForTheFeed()
    {
        var decision = Decide(hasDevice: false);

        Assert.True(decision.Create);
        Assert.Equal(NotificationStatus.Suppressed, decision.Status);
        Assert.Equal("NoDevice", decision.FailureReason);
        Assert.Equal(Now, decision.ScheduledFor);
    }

    [Fact]
    public void ARepeatIsSkippedEvenWithoutADevice() =>
        Assert.Equal(DispatchSkip.Duplicate, Decide(hasDevice: false, duplicate: true).Skip);

    [Fact]
    public void InsideQuietHoursTheNotificationIsScheduledForTheirEnd()
    {
        // 15:00 in Istanbul; the quiet hours 14:00-16:30 end at 13:30 UTC.
        var decision = Decide(preference: Preferences(quietStart: new TimeOnly(14, 0), quietEnd: new TimeOnly(16, 30)));

        Assert.Equal(NotificationStatus.Pending, decision.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 13, 30, 0, TimeSpan.Zero), decision.ScheduledFor);
    }

    [Fact]
    public void OutsideQuietHoursTheNotificationIsDueNow()
    {
        var decision = Decide(preference: Preferences(quietStart: new TimeOnly(22, 0), quietEnd: new TimeOnly(8, 0)));

        Assert.Equal(Now, decision.ScheduledFor);
    }

    [Fact]
    public void QuietHoursDoNotApplyToANotificationWithoutADevice()
    {
        var decision = Decide(preference: Preferences(quietStart: new TimeOnly(14, 0), quietEnd: new TimeOnly(16, 30)), hasDevice: false);

        Assert.Equal(Now, decision.ScheduledFor);
    }

    [Theory]
    [InlineData("PriceDrop", "PriceDrop")]
    [InlineData("BackInStock", "BackInStock")]
    [InlineData("AnyChange", "AnyChange")]
    public void AlertKindsMapToTheNotificationKindsOfTheSameName(string alert, string expected) =>
        Assert.Equal(Enum.Parse<NotificationKind>(expected), NotificationKinds.From(Enum.Parse<AlertKind>(alert)));

    [Fact]
    public void TheDedupeKeyNamesTheKindAndTheProduct()
    {
        var productId = Guid.NewGuid();

        Assert.Equal($"pricedrop:{productId}", NotificationKinds.AlertDedupeKey(NotificationKind.PriceDrop, productId));
        Assert.Equal($"backinstock:{productId}", NotificationKinds.AlertDedupeKey(NotificationKind.BackInStock, productId));
        Assert.Equal($"anychange:{productId}", NotificationKinds.AlertDedupeKey(NotificationKind.AnyChange, productId));
    }
}
