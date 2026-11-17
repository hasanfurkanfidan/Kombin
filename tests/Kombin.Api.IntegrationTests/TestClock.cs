using System.Globalization;
using Kombin.Api.Common.Time;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The integration tests' "real" clock: the system clock, or, with <c>KOMBIN_TEST_ISTANBUL_TIME=HH:mm</c>, the system clock shifted so that the
/// run starts at that time of day in Istanbul (the nearest such instant, at most 12 hours away) and runs on from there. The API sees the same
/// time (<see cref="TestHeaderClock"/> falls back to it), so a run can be made to start in the morning, just before or after the league's 21:00
/// close, or just after midnight, without touching the machine's clock or time zone. Unset (CI, every normal run): no shift at all.
/// A test that depends on the time of day must not rely on this: it pins its own time (<c>X-Test-Now</c>, or a job's <c>now</c> parameter).
/// </summary>
internal static class TestClock
{
    public const string Variable = "KOMBIN_TEST_ISTANBUL_TIME";

    private static readonly DateTimeOffset RealStart = DateTimeOffset.UtcNow;

    public static TimeSpan Shift { get; } = ShiftFor(Environment.GetEnvironmentVariable(Variable), RealStart);

    /// <summary>The run's first instant on this clock (exactly <c>HH:mm</c> in Istanbul when shifted).</summary>
    public static DateTimeOffset StartedAt => RealStart + Shift;

    public static bool IsShifted => Shift != TimeSpan.Zero;

    public static DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Shift;

    /// <summary>The calendar day in Istanbul now (the day the API calls today).</summary>
    public static DateOnly IstanbulToday => Istanbul.Today(UtcNow);

    /// <summary>
    /// 12:00 Istanbul on the current day: the pinned time (<c>client.At(TestClock.IstanbulNoon)</c>) of a test that compares a day it works out
    /// with the day the API works out, so a run crossing midnight, or the league's 21:00, never splits them.
    /// </summary>
    public static DateTimeOffset IstanbulNoon => Instant(IstanbulToday, new TimeOnly(12, 0));

    private static TimeSpan ShiftFor(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        if (!TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            throw new InvalidOperationException($"{Variable} must be HH:mm, was '{value}'.");
        }

        var today = Istanbul.Today(now);
        return new[] { -1, 0, 1 }
            .Select(days => Instant(today.AddDays(days), time) - now)
            .MinBy(shift => shift.Duration());
    }

    private static DateTimeOffset Instant(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time);
        return new DateTimeOffset(local, Istanbul.Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
