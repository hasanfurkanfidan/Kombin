using Kombin.Api.Features.Notification;

namespace Kombin.Api.Features.Wardrobe;

/// <summary>
/// Wearing is a set of days per item: marking a day twice is the same as once, and undoing removes the day. <c>WearCount</c> and
/// <c>LastWornAt</c> are always recomputed from that set (<see cref="Summarize"/>), never incremented, so they cannot drift.
/// </summary>
internal static class WearRules
{
    /// <summary>Days before this are refused as typos.</summary>
    public static readonly DateOnly Earliest = new(2000, 1, 1);

    /// <summary>The calendar day in Europe/Istanbul at <paramref name="now"/>; "today" for the default <c>wornOn</c>.</summary>
    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, QuietHours.Zone).DateTime);

    /// <summary>The day to record, or an error message: the default is today, a future day or one before <see cref="Earliest"/> is refused.</summary>
    public static (DateOnly Day, string? Error) Resolve(DateOnly? requested, DateOnly today)
    {
        var day = requested ?? today;

        if (day > today)
        {
            return (day, "'Worn On' cannot be in the future.");
        }

        return day < Earliest ? (day, $"'Worn On' must not be before {Earliest:yyyy-MM-dd}.") : (day, null);
    }

    public static (int WearCount, DateOnly? LastWornAt) Summarize(IEnumerable<DateOnly> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        var distinct = days.Distinct().ToList();

        return (distinct.Count, distinct.Count == 0 ? null : distinct.Max());
    }
}
