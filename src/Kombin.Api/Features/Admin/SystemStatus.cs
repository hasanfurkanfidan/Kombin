using System.Diagnostics;
using Kombin.Api.Common.Diagnostics;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Vision;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Admin;

/// <summary>Unhandled request errors of one route template in the last 24 hours.</summary>
internal sealed record RouteErrors(string Route, int Count, DateTimeOffset LastAt, string LastErrorCode);

/// <summary>Background jobs that failed for the last time (retries used up), by job name, over the 30 days the log keeps.</summary>
internal sealed record JobFailures(string Job, int Last24Hours, int Last30Days, DateTimeOffset LastAt, string LastErrorCode);

/// <summary>The newest offer a worker sent for one seller: when that seller's source last delivered.</summary>
internal sealed record WorkerSource(string Seller, DateTimeOffset LastSeenAt, int Offers);

internal sealed record VisionStatus(VisionHealth Health, long ResponseMilliseconds);

internal sealed record SystemStatusData(
    DateTimeOffset Now,
    IReadOnlyList<RouteErrors> RequestErrors,
    IReadOnlyList<JobFailures> JobFailures,
    VisionStatus Vision,
    DateOnly? LastDayClosed,
    DateTimeOffset? LastDayClosedAt,
    DateOnly? LastWeekClosed,
    DateTimeOffset? LastWeekClosedAt,
    IReadOnlyList<WorkerSource> Workers,
    bool SentryEnabled,
    string? SentryProjectUrl);

/// <summary>
/// The numbers of <c>/admin/system</c>. Counts, codes and times only, never a user: the errors come from <c>admin.error_events</c> (written by
/// <see cref="ErrorLog"/>), the league closes from <c>league.days</c> / <c>league.weeks</c>, the workers from the newest <c>last_seen_at</c> of each
/// seller's offers, vision from a live <c>GET /health</c>.
/// </summary>
internal sealed class SystemStatus(KombinDbContext db, IClock clock, IVisionClient vision, IConfiguration configuration)
{
    public async Task<SystemStatusData> LoadAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var dayAgo = now.AddDays(-1);
        var keptSince = now - ErrorLog.Retention;

        var requests = await db.ErrorEvents.AsNoTracking()
            .Where(error => error.Kind == ErrorLog.RequestKind && error.CreatedAt >= dayAgo)
            .Select(error => new { error.Source, error.ErrorCode, error.CreatedAt })
            .ToListAsync(cancellationToken);

        var jobs = await db.ErrorEvents.AsNoTracking()
            .Where(error => error.Kind == ErrorLog.JobKind && error.CreatedAt >= keptSince)
            .Select(error => new { error.Source, error.ErrorCode, error.CreatedAt })
            .ToListAsync(cancellationToken);

        var requestErrors = requests
            .GroupBy(error => error.Source, StringComparer.Ordinal)
            .Select(group =>
            {
                var last = group.MaxBy(error => error.CreatedAt)!;
                return new RouteErrors(group.Key, group.Count(), last.CreatedAt, last.ErrorCode);
            })
            .OrderByDescending(route => route.Count)
            .ThenBy(route => route.Route, StringComparer.Ordinal)
            .ToList();

        var jobFailures = jobs
            .GroupBy(error => error.Source, StringComparer.Ordinal)
            .Select(group =>
            {
                var last = group.MaxBy(error => error.CreatedAt)!;
                return new JobFailures(group.Key, group.Count(error => error.CreatedAt >= dayAgo), group.Count(), last.CreatedAt, last.ErrorCode);
            })
            .OrderByDescending(job => job.LastAt)
            .ToList();

        var lastDay = await db.LeagueDays.AsNoTracking()
            .Where(day => day.ClosedAt != null)
            .OrderByDescending(day => day.Day)
            .Select(day => new { day.Day, day.ClosedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var lastWeek = await db.LeagueWeeks.AsNoTracking()
            .Where(week => week.ClosedAt != null)
            .OrderByDescending(week => week.WeekStart)
            .Select(week => new { week.WeekStart, week.ClosedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var workers = await db.Offers.AsNoTracking()
            .Join(db.Sellers, offer => offer.SellerId, seller => seller.Id, (offer, seller) => new { seller.Slug, offer.LastSeenAt })
            .GroupBy(row => row.Slug)
            .Select(group => new WorkerSource(group.Key, group.Max(row => row.LastSeenAt), group.Count()))
            .ToListAsync(cancellationToken);

        return new SystemStatusData(
            now,
            requestErrors,
            jobFailures,
            await VisionAsync(cancellationToken),
            lastDay?.Day,
            lastDay?.ClosedAt,
            lastWeek?.WeekStart,
            lastWeek?.ClosedAt,
            [.. workers.OrderByDescending(worker => worker.LastSeenAt)],
            ErrorReporting.IsEnabled(configuration),
            ProjectUrl(configuration));
    }

    private async Task<VisionStatus> VisionAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var health = await vision.CheckHealthAsync(cancellationToken);
        return new VisionStatus(health, watch.ElapsedMilliseconds);
    }

    /// <summary>Only an https address is linked: the value comes from configuration, but the page must never carry a <c>javascript:</c> link.</summary>
    internal static string? ProjectUrl(IConfiguration configuration) =>
        Uri.TryCreate(configuration[ErrorReporting.ProjectUrlKey], UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
}
