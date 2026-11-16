namespace Kombin.Api.Common.Time;

/// <summary>
/// The product's time zone, Europe/Istanbul: quiet hours, every recurring job's schedule, calendar days (wears, the analytics cap, the league)
/// are read in it. Stored instants stay UTC. The container image must carry tzdata.
/// </summary>
internal static class Istanbul
{
    public const string ZoneId = "Europe/Istanbul";

    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

    /// <summary>The calendar day in Istanbul at <paramref name="now"/>.</summary>
    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);
}
