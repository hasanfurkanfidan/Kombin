namespace Kombin.Api.Features.Notification;

/// <summary>
/// Quiet hours are wall-clock times in Europe/Istanbul. A notification that is due inside them is held until they end.
/// An interval whose start is later than its end wraps past midnight (23:00-08:00).
/// </summary>
internal static class QuietHours
{
    public const string ZoneId = "Europe/Istanbul";

    /// <summary>The zone the user's quiet hours and the weekly schedule are read in. The container image must carry tzdata.</summary>
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

    /// <summary>When a notification due at <paramref name="now"/> may go out: <paramref name="now"/> itself, or the end of the quiet hours it falls into.</summary>
    public static DateTimeOffset NextAllowed(DateTimeOffset now, TimeOnly? start, TimeOnly? end, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (start is not { } from || end is not { } to || from == to)
        {
            return now;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        var wraps = from > to;

        var inside = wraps ? time >= from || time < to : time >= from && time < to;
        if (!inside)
        {
            return now;
        }

        // Past midnight already (or never wrapped): the end is today. Before midnight in a wrapping interval: tomorrow.
        var endDate = wraps && time >= from ? local.Date.AddDays(1) : local.Date;
        var endLocal = DateTime.SpecifyKind(endDate + to.ToTimeSpan(), DateTimeKind.Unspecified);

        // UTC, because that is the only offset Npgsql writes to a timestamptz column.
        return new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)).ToUniversalTime();
    }
}
