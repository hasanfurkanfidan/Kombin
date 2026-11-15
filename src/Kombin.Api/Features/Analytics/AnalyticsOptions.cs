using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Analytics;

/// <summary>
/// <c>Analytics:*</c>: how much the public event intake may store (security review M6). The intake is anonymous, so without a cap one script
/// could fill the database; the retention job keeps the table from growing for ever.
/// </summary>
internal sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Events stored per actor (the signed-in user, else the <c>anonymousId</c>) per calendar day in Europe/Istanbul; the rest is dropped.</summary>
    public int DailyEventCapPerActor { get; set; } = 5_000;

    /// <summary>Raw events older than this many days (by <c>received_at</c>) are deleted by <c>analytics-retention</c>; weekly metrics stay.</summary>
    public int RetentionDays { get; set; } = 180;
}

internal sealed class AnalyticsOptionsValidator : IValidateOptions<AnalyticsOptions>
{
    public ValidateOptionsResult Validate(string? name, AnalyticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Retention must outlive the metrics' cohort weeks (4 weeks plus D7) so a run never loses what it still computes.
        return options.DailyEventCapPerActor >= 1 && options.RetentionDays >= 60
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Analytics: DailyEventCapPerActor must be at least 1 and RetentionDays at least 60.");
    }
}

/// <summary>The analytics counters an operator can scrape (<c>System.Diagnostics.Metrics</c>, meter <c>Kombin.Analytics</c>).</summary>
internal static class AnalyticsMetrics
{
    public const string MeterName = "Kombin.Analytics";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Events dropped because their actor reached <see cref="AnalyticsOptions.DailyEventCapPerActor"/>.</summary>
    public static readonly Counter<long> EventsCapped = Meter.CreateCounter<long>("kombin.analytics.events_capped", unit: "{event}", description: "Events dropped by the daily per-actor cap.");
}

/// <summary>The cap's arithmetic, pure.</summary>
internal static class EventCap
{
    /// <summary>Midnight in Europe/Istanbul of the day <paramref name="now"/> falls in, as a UTC instant.</summary>
    public static DateTimeOffset DayStart(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
    }

    /// <summary>How many more events the actor may store today.</summary>
    public static int Remaining(int storedToday, int cap) => Math.Max(0, cap - storedToday);
}
