using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Analytics.Jobs;

/// <summary>
/// Nightly: deletes raw analytics events received more than <see cref="AnalyticsOptions.RetentionDays"/> ago (security review M6), in batches,
/// so the public intake cannot grow the table for ever. <c>analytics.weekly_metrics</c> keeps the aggregates. Idempotent: deleted rows no longer
/// match. Like the other jobs it is a plain class; tests call <see cref="PurgeAsync"/> with their own anonymous IDs (the database is shared, and
/// the metrics tests seed a cohort in 2020).
/// </summary>
internal sealed partial class AnalyticsRetentionJob(KombinDbContext db, IClock clock, IOptions<AnalyticsOptions> options, ILogger<AnalyticsRetentionJob> logger)
{
    public const string JobId = "analytics-retention";

    /// <summary>Daily at 03:30 in Europe/Istanbul (the zone is set where the job is registered).</summary>
    public const string Cron = "30 3 * * *";

    public const int BatchSize = 5_000;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var deleted = await PurgeAsync(null, cancellationToken);

        LogRun(logger, deleted, options.Value.RetentionDays);
    }

    /// <summary>Deletes what is past retention; <paramref name="anonymousIds"/> restricts it to those devices' events (tests).</summary>
    public async Task<int> PurgeAsync(IReadOnlyCollection<string>? anonymousIds, CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-options.Value.RetentionDays);
        var deleted = 0;

        while (true)
        {
            var due = db.AnalyticsEvents.Where(row => row.ReceivedAt < cutoff);
            if (anonymousIds is not null)
            {
                due = due.Where(row => row.AnonymousId != null && anonymousIds.Contains(row.AnonymousId));
            }

            var batch = await due.OrderBy(row => row.Id).Take(BatchSize).Select(row => row.Id).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return deleted;
            }

            deleted += await db.AnalyticsEvents.Where(row => batch.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analytics retention: {Deleted} event(s) older than {Days} days deleted.")]
    private static partial void LogRun(ILogger logger, int deleted, int days);
}
