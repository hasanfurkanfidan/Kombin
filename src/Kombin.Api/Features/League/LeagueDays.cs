using Kombin.Api.Common.Time;

namespace Kombin.Api.Features.League;

/// <summary>
/// A league day is a calendar day in Europe/Istanbul that closes at <c>League:DayClosesAt</c> (21:00); a week runs Monday to Sunday. Every
/// boundary is half-open: something at 21:00:00 exactly is past the close. Pure.
/// </summary>
internal static class LeagueDays
{
    /// <summary>Istanbul midnight of <paramref name="day"/>, as an instant.</summary>
    public static DateTimeOffset Start(DateOnly day) => At(day, TimeOnly.MinValue);

    /// <summary>When votes stop and the day's outfit is fixed: <paramref name="closesAt"/> Istanbul time of <paramref name="day"/>, as an instant.</summary>
    public static DateTimeOffset ClosesAt(DateOnly day, TimeOnly closesAt) => At(day, closesAt);

    /// <summary>The Monday of <paramref name="day"/>'s week.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// The window in which a wardrobe piece counts for <paramref name="day"/>'s piece points: from the previous day's close to this day's,
    /// <c>[start, end)</c>. An evening addition belongs to the next day (design note section 9).
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) PieceWindow(DateOnly day, TimeOnly closesAt) =>
        (ClosesAt(day.AddDays(-1), closesAt), ClosesAt(day, closesAt));

    /// <summary>
    /// The days the hourly close looks at, oldest first: the latest day whose close has passed at <paramref name="now"/> and the
    /// <paramref name="catchUpDays"/> − 1 days before it.
    /// </summary>
    public static IReadOnlyList<DateOnly> CloseWindow(DateTimeOffset now, TimeOnly closesAt, int catchUpDays)
    {
        var today = Istanbul.Today(now);
        var latest = now >= ClosesAt(today, closesAt) ? today : today.AddDays(-1);

        return [.. Enumerable.Range(0, catchUpDays).Select(offset => latest.AddDays(offset - catchUpDays + 1))];
    }

    private static DateTimeOffset At(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);

        return new DateTimeOffset(local, Istanbul.Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
