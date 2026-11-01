using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Notification.Jobs;
using Kombin.Api.Features.Notification.RegisterDevice;
using Kombin.Api.Features.Notification.SendTest;
using Kombin.Api.Features.Notification.UpdatePreferences;

namespace Kombin.Api.Tests;

public class NotificationValidatorTests
{
    private readonly RegisterDeviceValidator _device = new();

    private readonly UpdatePreferencesValidator _preferences = new();

    private readonly SendTestValidator _test = new();

    private static UpdatePreferencesRequest Prefs(
        int? max = null, string? start = null, string? end = null, bool? clear = null) =>
        new(null, null, null, max, start, end, clear);

    [Theory]
    [InlineData("ExponentPushToken[abcDEF123-_xyz]", true)]
    [InlineData("ExpoPushToken[abc]", true)]
    [InlineData("ExponentPushToken[]", false)]
    [InlineData("ExponentPushToken[a b]", false)]
    [InlineData("ExponentPushToken[a[b]", false)]
    [InlineData("ExponentPushToken[abc", false)]
    [InlineData("exponentpushtoken[abc]", false)]
    [InlineData("Other[abc]", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PushTokensHaveToLookLikeExpoTokens(string? token, bool expected) =>
        Assert.Equal(expected, PushTokens.IsValid(token));

    [Fact]
    public void AnOverlongTokenIsInvalid()
    {
        Assert.True(PushTokens.IsValid($"ExponentPushToken[{new string('a', PushTokens.MaxLength - "ExponentPushToken[]".Length)}]"));
        Assert.False(PushTokens.IsValid($"ExponentPushToken[{new string('a', PushTokens.MaxLength)}]"));
    }

    [Theory]
    [InlineData("Ios")]
    [InlineData("android")]
    public void AKnownPlatformPasses(string platform) =>
        Assert.True(_device.Validate(new RegisterDeviceRequest("ExponentPushToken[abc]", platform, "1.0", "Pixel")).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("Windows")]
    [InlineData("0")]
    public void AnUnknownPlatformIsRejected(string platform)
    {
        var result = _device.Validate(new RegisterDeviceRequest("ExponentPushToken[abc]", platform, null, null));

        Assert.Equal("Platform", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void DeviceTextFieldsHaveLengthLimits()
    {
        var result = _device.Validate(new RegisterDeviceRequest("ExponentPushToken[abc]", "Ios", new string('v', 41), new string('n', 101)));

        Assert.Equal(["AppVersion", "DeviceName"], result.Errors.Select(error => error.PropertyName).Order());
    }

    [Fact]
    public void AnEmptyPreferencesUpdatePasses() =>
        Assert.True(_preferences.Validate(Prefs()).IsValid);

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(-1, false)]
    [InlineData(8, false)]
    public void TheWeeklyLimitIsBetweenZeroAndSeven(int limit, bool valid) =>
        Assert.Equal(valid, _preferences.Validate(Prefs(max: limit)).IsValid);

    [Theory]
    [InlineData("22:00", "08:00", true)]
    [InlineData("00:00", "23:59", true)]
    [InlineData("22:00", "22:00", false)]
    [InlineData("24:00", "08:00", false)]
    [InlineData("22:00", "8:00", false)]
    [InlineData("22:00", "08:00:00", false)]
    [InlineData("10pm", "08:00", false)]
    public void QuietHoursAreTimesOfDayThatDiffer(string start, string end, bool valid) =>
        Assert.Equal(valid, _preferences.Validate(Prefs(start: start, end: end)).IsValid);

    [Fact]
    public void ALoneQuietHoursBoundPassesTheValidatorBecauseTheHandlerAnswersItWithItsOwnErrorCode() =>
        Assert.True(_preferences.Validate(Prefs(start: "22:00")).IsValid);

    [Fact]
    public void ClearingQuietHoursCannotBeCombinedWithNewOnes()
    {
        Assert.True(_preferences.Validate(Prefs(clear: true)).IsValid);
        Assert.False(_preferences.Validate(Prefs(start: "22:00", end: "08:00", clear: true)).IsValid);
    }

    [Fact]
    public void TheTestNotificationNeedsAUserAndKeepsTextWithinTheColumns()
    {
        Assert.True(_test.Validate(new SendTestRequest(Guid.NewGuid(), null, null)).IsValid);
        Assert.False(_test.Validate(new SendTestRequest(Guid.Empty, null, null)).IsValid);
        Assert.False(_test.Validate(new SendTestRequest(Guid.NewGuid(), new string('t', 121), null)).IsValid);
        Assert.False(_test.Validate(new SendTestRequest(Guid.NewGuid(), null, new string('b', 301))).IsValid);
    }

    [Fact]
    public void TicketIdsAreJoinedUpToTheColumnLength()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString()).ToList();

        Assert.Equal(ids[0], PushSenderJob.JoinTicketIds(ids.Take(1)));
        Assert.Equal($"{ids[0]},{ids[1]}", PushSenderJob.JoinTicketIds(ids.Take(2)));

        // Three UUIDs need 110 characters; the third no longer fits the 100-character column.
        Assert.Equal($"{ids[0]},{ids[1]}", PushSenderJob.JoinTicketIds(ids));
        Assert.Equal(string.Empty, PushSenderJob.JoinTicketIds([]));
    }
}
