namespace Kombin.Api.Persistence.Entities.Analytics;

/// <summary>
/// One beta metric of one weekly cohort, written by the <c>weekly-metrics</c> job. The key is (<see cref="WeekStart"/>, <see cref="Metric"/>):
/// a rerun overwrites the row, so the numbers are "as of <see cref="ComputedAt"/>".
/// </summary>
internal sealed class WeeklyMetric
{
    /// <summary>The Monday (Europe/Istanbul) of the week in which the cohort's users first opened the app.</summary>
    public DateOnly WeekStart { get; set; }

    /// <summary>One of <c>MetricNames</c>.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>A percentage between 0 and 100, rounded to two decimals.</summary>
    public decimal Value { get; set; }

    public long Numerator { get; set; }

    public long Denominator { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}
