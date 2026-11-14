using System.Data;
using Hangfire;
using Hangfire.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Kombin.Api.Features.Admin;

/// <summary>A dashboard number: the figure(s) and what produced them. <c>Today</c>/<c>Week</c> are null for a metric that describes the present state, <c>Now</c> for a windowed one.</summary>
internal sealed record DashboardFigure(DashboardMetric Metric, long? Today, long? Week, long? Now);

internal sealed record DashboardBreakdownFigure(DashboardBreakdown Breakdown, IReadOnlyDictionary<string, long> Today, IReadOnlyDictionary<string, long> Week);

/// <summary>A Hangfire recurring job as the health strip shows it. <c>LastState</c> is the last run's state (<c>Succeeded</c>, <c>Failed</c>, <c>Processing</c>) or null if it never ran.</summary>
internal sealed record JobHealth(string Id, string Cron, DateTimeOffset? LastRun, string? LastState, DateTimeOffset? NextRun, string? Error);

internal sealed record HealthStrip(bool HangfireEnabled, string? HangfireError, IReadOnlyList<JobHealth> Jobs, VisionHealth Vision, long? DatabaseBytes);

internal sealed record DashboardData(
    DateTimeOffset TodayStart,
    DateTimeOffset WeekStart,
    IReadOnlyList<DashboardFigure> Figures,
    IReadOnlyList<DashboardBreakdownFigure> Breakdowns,
    HealthStrip Health);

/// <summary>Runs the queries of <see cref="DashboardQueries"/> and gathers the health strip (Hangfire recurring jobs, the vision service, the database size).</summary>
internal sealed class AdminDashboard(KombinDbContext db, IClock clock, IVisionClient vision, IConfiguration configuration, IServiceProvider services)
{
    public async Task<DashboardData> LoadAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var (todayStart, weekStart) = Windows(now);

        var figures = new List<DashboardFigure>();
        foreach (var metric in DashboardQueries.Metrics)
        {
            figures.Add(metric.Windowed
                ? new DashboardFigure(metric, await ScalarAsync(metric.Sql, todayStart, cancellationToken), await ScalarAsync(metric.Sql, weekStart, cancellationToken), null)
                : new DashboardFigure(metric, null, null, await ScalarAsync(metric.Sql, null, cancellationToken)));
        }

        var breakdowns = new List<DashboardBreakdownFigure>();
        foreach (var breakdown in DashboardQueries.Breakdowns)
        {
            breakdowns.Add(new DashboardBreakdownFigure(
                breakdown,
                await BreakdownAsync(breakdown.Sql, todayStart, cancellationToken),
                await BreakdownAsync(breakdown.Sql, weekStart, cancellationToken)));
        }

        return new DashboardData(todayStart, weekStart, figures, breakdowns, await LoadHealthAsync(cancellationToken));
    }

    /// <summary>"Today" starts at midnight in Istanbul (the founder's day, like the jobs' schedules); "7 days" is a rolling week.</summary>
    internal static (DateTimeOffset TodayStart, DateTimeOffset WeekStart) Windows(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, QuietHours.Zone);
        var midnight = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);

        return (midnight.ToUniversalTime(), now.AddDays(-7));
    }

    private async Task<HealthStrip> LoadHealthAsync(CancellationToken cancellationToken)
    {
        var hangfireEnabled = configuration.GetValue<bool>(NotificationModule.HangfireEnabledKey);
        var (jobs, hangfireError) = hangfireEnabled ? ReadRecurringJobs() : ([], null);
        var health = await vision.CheckHealthAsync(cancellationToken);

        long? size;
        try
        {
            size = await ScalarAsync(DashboardQueries.DatabaseSizeSql, null, cancellationToken);
        }
        catch (NpgsqlException)
        {
            size = null;
        }

        return new HealthStrip(hangfireEnabled, hangfireError, jobs, health, size);
    }

    /// <summary>Only touches the Hangfire storage when the server runs here: without it the <c>hangfire</c> schema may not exist (tests never create it).</summary>
    private (IReadOnlyList<JobHealth> Jobs, string? Error) ReadRecurringJobs()
    {
        try
        {
            var storage = services.GetRequiredService<JobStorage>();
            using var connection = storage.GetConnection();

            var jobs = connection.GetRecurringJobs()
                .OrderBy(job => job.Id, StringComparer.Ordinal)
                .Select(job => new JobHealth(
                    job.Id,
                    job.Cron,
                    ToOffset(job.LastExecution),
                    job.LastJobState,
                    ToOffset(job.NextExecution),
                    job.Error))
                .ToList();

            return (jobs, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ([], exception.Message);
        }
    }

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null;

    private async Task<long> ScalarAsync(string sql, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection.Connection, sql, since);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyDictionary<string, long>> BreakdownAsync(string sql, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection.Connection, sql, since);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetString(0)] = reader.GetInt64(1);
        }

        return rows;
    }

    private static NpgsqlCommand Command(IDbConnection connection, string sql, DateTimeOffset? since)
    {
        var command = (NpgsqlCommand)connection.CreateCommand();
        command.CommandText = sql;

        if (since is { } start)
        {
            command.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = start.ToUniversalTime() });
        }

        return command;
    }

    /// <summary>Borrows the context's connection (opening it if needed) and closes it again only if this call opened it.</summary>
    private async Task<BorrowedConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;

        if (opened)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        return new BorrowedConnection(db, connection, opened);
    }

    private readonly struct BorrowedConnection(KombinDbContext db, System.Data.Common.DbConnection connection, bool opened) : IAsyncDisposable
    {
        public System.Data.Common.DbConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
