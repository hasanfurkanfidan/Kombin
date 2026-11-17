using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.Features.Notification.Jobs;

/// <summary>What one run did: results handled, notifications created (pending or, without a device, suppressed), and results skipped for a preference or a player who left.</summary>
internal sealed record LeagueResultsSummary(int Handled, int Created, int Skipped);

/// <summary>
/// <c>league-results</c>, every 5 minutes: the evening result push of Kombin Ligi (design note sections 1 and 14), the league's only notification.
/// It reads the league's result rows (<c>league.daily_scores</c>, an entity: Notification calls no league code) of days closed in the last
/// <see cref="Window"/> that have not been handled (<c>notified_at</c> null), and for each:
/// <list type="bullet">
/// <item>a player who has left the league, is disabled or whose deletion is pending, and a result removed by a review: handled, nothing sent;</item>
/// <item>a result under review: left for later (sent if a reviewer restores it within the window; never after it, because the text says "bugün");</item>
/// <item>otherwise the alert dispatcher's rules (<see cref="DispatchRules.Decide"/>): the <c>LeagueResults</c> preference off → handled, no row; no
/// active device → a <c>Suppressed</c> row for the in-app feed; else a <c>Pending</c> row scheduled now or at the end of the quiet hours, which
/// <c>push-sender</c> delivers. The text is the owner's (<see cref="Copy.ForLeagueResult"/>): the rank when ranked, "yeterli oy toplanamadı"
/// otherwise, and the day's total.</item>
/// </list>
/// <c>notified_at</c> is set in the same transaction as the notification row, and the dedupe key is <c>league:{day}</c>, so a player gets at most
/// one result push per day, however often the job runs. Plain class like the other jobs; tests call <see cref="SendAsync"/> with their own users.
/// </summary>
internal sealed partial class LeagueResultsJob(KombinDbContext db, IClock clock, ILogger<LeagueResultsJob> logger)
{
    public const string JobId = "league-results";

    public const string Cron = "*/5 * * * *";

    public const int Batch = 500;

    /// <summary>How long after a day's close its results are still pushed (21:00 to 09:00): later, "Bugünkü kombinin" would be wrong.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(12);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await SendAsync(clock.UtcNow, null, cancellationToken);
        if (summary.Handled > 0)
        {
            LogRun(logger, summary.Handled, summary.Created, summary.Skipped);
        }
    }

    /// <summary><paramref name="userIds"/> limits the run to those players (tests; the database is shared).</summary>
    public async Task<LeagueResultsSummary> SendAsync(DateTimeOffset now, IReadOnlyCollection<Guid>? userIds, CancellationToken cancellationToken)
    {
        var since = now - Window;
        var (handled, created, skipped) = (0, 0, 0);

        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var due = db.LeagueDailyScores
                .Where(score => score.NotifiedAt == null && !score.UnderReview)
                .Where(score => db.LeagueDays.Any(day => day.Day == score.Day && day.ClosedAt != null && day.ClosedAt >= since && day.ClosedAt <= now));
            if (userIds is not null)
            {
                due = due.Where(score => userIds.Contains(score.UserId));
            }

            var rows = await due.OrderBy(score => score.Day).ThenBy(score => score.UserId).Take(Batch).ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                return new LeagueResultsSummary(handled, created, skipped);
            }

            var ids = rows.Select(row => row.UserId).Distinct().ToList();
            var playing = (await db.LeaguePlayers.AsNoTracking()
                .Where(player => ids.Contains(player.UserId) && player.LeftAt == null)
                .Where(player => db.Users.Any(user => user.Id == player.UserId && user.DisabledAt == null && user.DeletionRequestedAt == null))
                .Select(player => player.UserId)
                .ToListAsync(cancellationToken)).ToHashSet();
            var preferences = await db.NotificationPreferences.AsNoTracking()
                .Where(preference => ids.Contains(preference.UserId))
                .ToDictionaryAsync(preference => preference.UserId, cancellationToken);
            var withDevice = (await db.Devices.AsNoTracking()
                .Where(device => ids.Contains(device.UserId) && device.IsActive)
                .Select(device => device.UserId)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();

            foreach (var row in rows)
            {
                row.NotifiedAt = now;
                handled++;

                var preference = preferences.GetValueOrDefault(row.UserId);
                var decision = DispatchRules.Decide(NotificationKind.LeagueResult, preference, withDevice.Contains(row.UserId), false, now, Istanbul.Zone);
                if (!playing.Contains(row.UserId) || row.RemovedByReview || !decision.Create)
                {
                    skipped++;
                    continue;
                }

                var text = Copy.ForLeagueResult(row.Ranked ? row.Rank : null, row.Total);
                db.Notifications.Add(new NotificationEntity
                {
                    UserId = row.UserId,
                    Kind = NotificationKind.LeagueResult,
                    Title = text.Title,
                    Body = text.Body,
                    Data = NotificationData.ForLeagueResult(row.Day),
                    DedupeKey = $"league:{row.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}",
                    CreatedAt = now,
                    ScheduledFor = decision.ScheduledFor,
                    Status = decision.Status,
                    FailureReason = decision.FailureReason,
                });
                created++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();

            if (rows.Count < Batch)
            {
                return new LeagueResultsSummary(handled, created, skipped);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "league-results: {Handled} results handled, {Created} notifications created, {Skipped} skipped.")]
    private static partial void LogRun(ILogger logger, int handled, int created, int skipped);
}
