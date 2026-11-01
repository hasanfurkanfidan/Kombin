using Kombin.Api.Features.Notification;

namespace Kombin.Api.Tests;

public class QuietHoursTests
{
    private static readonly TimeZoneInfo Istanbul = QuietHours.Zone;

    /// <summary>Istanbul is UTC+3 all year, so a local time is written here as the UTC instant it names.</summary>
    private static DateTimeOffset Utc(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    private static readonly TimeOnly Ten = new(22, 0);

    private static readonly TimeOnly Eight = new(8, 0);

    [Fact]
    public void TheZoneIsIstanbul()
    {
        Assert.Equal("Europe/Istanbul", QuietHours.ZoneId);
        Assert.Equal(TimeSpan.FromHours(3), Istanbul.GetUtcOffset(Utc(1, 12)));
    }

    [Fact]
    public void WithoutQuietHoursNothingIsHeld()
    {
        var now = Utc(1, 12);

        Assert.Equal(now, QuietHours.NextAllowed(now, null, null, Istanbul));
        Assert.Equal(now, QuietHours.NextAllowed(now, Ten, null, Istanbul));
        Assert.Equal(now, QuietHours.NextAllowed(now, null, Eight, Istanbul));
        Assert.Equal(now, QuietHours.NextAllowed(now, Ten, Ten, Istanbul));
    }

    [Fact]
    public void OutsideAnOvernightWindowNothingIsHeld()
    {
        var afternoon = Utc(1, 12);

        Assert.Equal(afternoon, QuietHours.NextAllowed(afternoon, Ten, Eight, Istanbul));
    }

    [Fact]
    public void BeforeMidnightInAnOvernightWindowTheNotificationWaitsUntilTomorrowMorning()
    {
        // 23:30 in Istanbul on the 1st.
        var now = Utc(1, 20, 30);

        Assert.Equal(Utc(2, 5), QuietHours.NextAllowed(now, Ten, Eight, Istanbul));
    }

    [Fact]
    public void AfterMidnightInAnOvernightWindowTheNotificationWaitsUntilThisMorning()
    {
        // 06:00 in Istanbul on the 1st.
        var now = Utc(1, 3);

        Assert.Equal(Utc(1, 5), QuietHours.NextAllowed(now, Ten, Eight, Istanbul));
    }

    [Fact]
    public void TheStartIsInsideAndTheEndIsOutside()
    {
        var atStart = Utc(1, 19);
        var atEnd = Utc(1, 5);

        Assert.Equal(Utc(2, 5), QuietHours.NextAllowed(atStart, Ten, Eight, Istanbul));
        Assert.Equal(atEnd, QuietHours.NextAllowed(atEnd, Ten, Eight, Istanbul));
    }

    [Fact]
    public void ADaytimeWindowEndsTheSameDay()
    {
        var start = new TimeOnly(13, 0);
        var end = new TimeOnly(15, 0);

        // 14:00 in Istanbul is inside; 16:00 is not.
        Assert.Equal(Utc(1, 12), QuietHours.NextAllowed(Utc(1, 11), start, end, Istanbul));
        Assert.Equal(Utc(1, 13), QuietHours.NextAllowed(Utc(1, 13), start, end, Istanbul));
    }

    [Fact]
    public void TheResultIsInUtcSoItCanBeStored() =>
        Assert.Equal(TimeSpan.Zero, QuietHours.NextAllowed(Utc(1, 20, 30), Ten, Eight, Istanbul).Offset);

    [Fact]
    public void TheLocalDateDecidesNotTheUtcDate()
    {
        // 23:30 UTC on the 1st is 02:30 on the 2nd in Istanbul: inside the window, ending at 08:00 on the 2nd (05:00 UTC).
        Assert.Equal(Utc(2, 5), QuietHours.NextAllowed(Utc(1, 23, 30), Ten, Eight, Istanbul));
    }
}
