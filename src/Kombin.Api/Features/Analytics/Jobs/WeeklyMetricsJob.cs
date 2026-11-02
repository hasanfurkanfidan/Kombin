using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Notification;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Analytics;

namespace Kombin.Api.Features.Analytics.Jobs;

/// <summary>
/// Monday morning: computes the four beta metrics (<c>docs/metrics.sql</c>) for last week's cohort into <c>analytics.weekly_metrics</c>.
/// It also recomputes the <see cref="CohortWeeks"/> - 1 weeks before that, because D7 retention of a cohort cannot be known until
/// days 6 to 8 after its last member's first visit are over: the row of a young cohort covers only the members that are old enough
/// (its denominator says how many) and is completed by the following runs. Idempotent: a rerun overwrites the rows, the numbers are
/// "as of <c>computed_at</c>".
/// </summary>
internal sealed partial class WeeklyMetricsJob(KombinDbContext db, IClock clock, ILogger<WeeklyMetricsJob> logger)
{
    public const string JobId = "weekly-metrics";

    /// <summary>Monday 08:00 in <see cref="QuietHours.ZoneId"/> (the zone is set where the job is registered, in <c>UseAnalyticsJobs</c>).</summary>
    public const string Cron = "0 8 * * 1";

    /// <summary>How many cohort weeks each run covers: last week plus the earlier ones whose D7 retention is still filling in.</summary>
    public const int CohortWeeksPerRun = 4;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var written = await ComputeAsync(clock.UtcNow, cancellationToken);
        LogComputed(logger, written);
    }

    /// <summary>Computes the cohort weeks before <paramref name="asOf"/> and returns how many (week, metric) rows it wrote. A metric with an empty cohort writes nothing.</summary>
    public async Task<int> ComputeAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var weeks = CohortWeeks.Previous(asOf, CohortWeeksPerRun, QuietHours.Zone);
        var weekStarts = weeks.Select(week => week.WeekStart).ToList();

        var existing = await db.WeeklyMetrics
            .Where(row => weekStarts.Contains(row.WeekStart))
            .ToDictionaryAsync(row => (row.WeekStart, row.Metric), cancellationToken);

        var written = 0;

        foreach (var week in weeks)
        {
            foreach (var metric in MetricNames.All)
            {
                var counts = await MetricSql.RunAsync(db, metric, week.Start, week.End, asOf, cancellationToken);

                if (counts.Denominator == 0)
                {
                    continue;
                }

                if (!existing.TryGetValue((week.WeekStart, metric), out var row))
                {
                    row = new WeeklyMetric { WeekStart = week.WeekStart, Metric = metric };
                    db.WeeklyMetrics.Add(row);
                }

                row.Numerator = counts.Numerator;
                row.Denominator = counts.Denominator;
                row.Value = Math.Round(counts.Numerator * 100m / counts.Denominator, 2, MidpointRounding.AwayFromZero);
                row.ComputedAt = asOf;
                written++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return written;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Weekly metrics: {Count} row(s) written.")]
    private static partial void LogComputed(ILogger logger, int count);
}
