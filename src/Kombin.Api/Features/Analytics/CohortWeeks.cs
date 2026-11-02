namespace Kombin.Api.Features.Analytics;

/// <summary>A cohort week: the Monday that names it and the instants it spans, [<paramref name="Start"/>, <paramref name="End"/>).</summary>
internal readonly record struct CohortWeek(DateOnly WeekStart, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Weeks run Monday to Monday in the product's time zone (Europe/Istanbul), the same zone the schedule uses.</summary>
internal static class CohortWeeks
{
    /// <summary>
    /// The <paramref name="count"/> complete weeks before the one <paramref name="now"/> falls in, the most recent first.
    /// On a Monday morning the first of them is "last week".
    /// </summary>
    public static IReadOnlyList<CohortWeek> Previous(DateTimeOffset now, int count, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        return
        [
            .. Enumerable.Range(1, count).Select(weeksBack =>
            {
                var monday = thisMonday.AddDays(-7 * weeksBack);

                return new CohortWeek(monday, StartOfDay(monday, zone), StartOfDay(monday.AddDays(7), zone));
            }),
        ];
    }

    private static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
}
