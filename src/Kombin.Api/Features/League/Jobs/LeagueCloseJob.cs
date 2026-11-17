using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.Jobs;

/// <summary>What one run did: days closed, days found closed by someone else meanwhile, whether the lock was taken elsewhere, failures.</summary>
internal sealed record LeagueCloseSummary(IReadOnlyList<DateOnly> Closed, bool LockBusy, int Failures);

/// <summary>
/// <c>league-close</c>, hourly at :00 Europe/Istanbul (design note section 8). The 21:00 run does the work; every other run looks at the last
/// <c>League:CloseCatchUpDays</c> days and closes, oldest first, each one whose close time has passed and that is not closed yet, so a deploy or
/// a crash at 21:00 only makes the results late, without relying on Hangfire's handling of missed runs. A day with no row (nobody voted) is
/// closed too: every entry unranked, the usage points still given. It stops at the first failure, because a later day's weekly piece cap reads
/// the earlier days' rows, and logs it; the next run tries again. A day older than the window that is still open is logged as a warning (close
/// it with <c>POST /internal/league/days/{date}/close</c>). The lock is <see cref="DayCloser"/>'s advisory transaction lock: a run that does not
/// get it ends successfully. Plain class like the other jobs; does nothing while <c>League:Enabled</c> is false. Tests call
/// <see cref="CloseDueAsync"/> with their own time.
/// </summary>
internal sealed partial class LeagueCloseJob(DayCloser closer, KombinDbContext db, IClock clock, IOptions<LeagueOptions> options, ILogger<LeagueCloseJob> logger)
{
    public const string JobId = "league-close";

    public const string Cron = "0 * * * *";

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await CloseDueAsync(clock.UtcNow, cancellationToken);
    }

    public async Task<LeagueCloseSummary> CloseDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var window = LeagueDays.CloseWindow(now, settings.DayClosesAt, settings.CloseCatchUpDays);
        var closedAlready = await db.LeagueDays.AsNoTracking()
            .Where(row => window.Contains(row.Day) && row.ClosedAt != null)
            .Select(row => row.Day)
            .ToListAsync(cancellationToken);

        var closed = new List<DateOnly>();
        var (busy, failures) = (false, 0);

        foreach (var day in window.Except(closedAlready))
        {
            try
            {
                var result = await closer.CloseAsync(day, now, cancellationToken);
                if (result.Outcome == DayCloseOutcome.AlreadyRunning)
                {
                    busy = true;
                    LogBusy(logger, day);
                    break;
                }

                if (result.Outcome == DayCloseOutcome.Closed)
                {
                    closed.Add(day);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                LogFailed(logger, day, exception);
                db.ChangeTracker.Clear();
                break;
            }
        }

        var stale = await db.LeagueDays.AsNoTracking().CountAsync(row => row.ClosedAt == null && row.Day < window[0], cancellationToken);
        if (stale > 0)
        {
            LogStale(logger, stale, window[0]);
        }

        return new LeagueCloseSummary(closed, busy, failures);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "league-close: another close holds the lock; {Day} is left to it or to the next run.")]
    private static partial void LogBusy(ILogger logger, DateOnly day);

    [LoggerMessage(Level = LogLevel.Error, Message = "league-close: closing {Day} failed and nothing of it was written; the next run tries again.")]
    private static partial void LogFailed(ILogger logger, DateOnly day, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "league-close: {Count} league day(s) before {WindowStart} are still open; close them with POST /internal/league/days/{{date}}/close.")]
    private static partial void LogStale(ILogger logger, int count, DateOnly windowStart);
}
