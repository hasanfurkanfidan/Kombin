using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.Jobs;

/// <summary>What one run did: players resolved (and failed), entries now competing or waiting for the safety check, image copies deleted.</summary>
internal sealed record LeagueEntriesSummary(int Resolved, int Competing, int Pending, int ModerationCalls, int CopiesDeleted, int Failures);

/// <summary>
/// <c>league-entries</c>, every minute (design note section 5): nothing in Recommendation calls the league, so the league looks. It
/// <list type="number">
/// <item>deletes the image copies of withdrawn and removed entries (leaving withdraws today's entry), so they live minutes after that;</item>
/// <item>resolves every joined player with a <c>Worn</c> outfit feedback row from the last <see cref="LookBack"/> (idempotent, tolerant of a
/// missed minute; a watermark on the ID was rejected because IDs are not committed in order) and every player whose entry of today still waits
/// for the image safety check, asking the safety filter for at most <c>League:ModerationRetriesPerRun</c> pieces per run.</item>
/// </list>
/// Plain class like the other jobs; does nothing while <c>League:Enabled</c> is false. Tests call <see cref="ProcessAsync"/> with their own time
/// and players. One player's failure is logged and does not stop the others.
/// </summary>
internal sealed partial class LeagueEntriesJob(
    KombinDbContext db, EntryMaterializer materializer, IClock clock, IOptions<LeagueOptions> options, ILogger<LeagueEntriesJob> logger)
{
    public const string JobId = "league-entries";

    public const string Cron = "* * * * *";

    /// <summary>How far back a wear is looked for: ten runs, so a few missed minutes lose nothing.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromMinutes(10);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var summary = await ProcessAsync(clock.UtcNow, null, cancellationToken);
        if (summary.Resolved > 0 || summary.CopiesDeleted > 0 || summary.Failures > 0)
        {
            LogRun(logger, summary.Resolved, summary.Competing, summary.Pending, summary.ModerationCalls, summary.CopiesDeleted, summary.Failures);
        }
    }

    /// <summary><paramref name="userIds"/> scopes the run to those players (tests; the database is shared).</summary>
    public async Task<LeagueEntriesSummary> ProcessAsync(DateTimeOffset now, IReadOnlyCollection<Guid>? userIds, CancellationToken cancellationToken)
    {
        var copiesDeleted = await materializer.ReleaseCopiesAsync(userIds, cancellationToken);

        var day = Istanbul.Today(now);
        var since = now - LookBack;
        var worn = db.OutfitFeedback.AsNoTracking()
            .Where(feedback => feedback.Action == OutfitFeedbackAction.Worn && feedback.CreatedAt >= since && feedback.CreatedAt <= now)
            .Where(feedback => db.LeaguePlayers.Any(player => player.UserId == feedback.UserId && player.LeftAt == null))
            .Select(feedback => feedback.UserId);
        var waiting = db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Day == day && entry.Status == LeagueEntryStatus.Pending && entry.UserId != null)
            .Select(entry => entry.UserId!.Value);

        if (userIds is not null)
        {
            worn = worn.Where(userId => userIds.Contains(userId));
            waiting = waiting.Where(userId => userIds.Contains(userId));
        }

        var players = (await worn.Distinct().ToListAsync(cancellationToken)).Union(await waiting.Distinct().ToListAsync(cancellationToken)).Order().ToList();
        var budget = options.Value.ModerationRetriesPerRun;
        var (resolved, competing, pending, calls, failures) = (0, 0, 0, 0, 0);

        foreach (var userId in players)
        {
            try
            {
                var resolution = await materializer.ResolveAsync(userId, now, Math.Max(0, budget - calls), cancellationToken);
                calls += resolution.ModerationCalls;
                resolved++;
                competing += resolution.Entry?.Status == LeagueEntryStatus.Competing ? 1 : 0;
                pending += resolution.Entry?.Status == LeagueEntryStatus.Pending ? 1 : 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                LogPlayerFailed(logger, userId, exception);
                db.ChangeTracker.Clear();
            }
        }

        return new LeagueEntriesSummary(resolved, competing, pending, calls, copiesDeleted, failures);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "league-entries: {Resolved} players resolved ({Competing} competing, {Pending} pending), {Calls} safety checks, {Copies} copies deleted, {Failures} failures.")]
    private static partial void LogRun(ILogger logger, int resolved, int competing, int pending, int calls, int copies, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "league-entries: player {UserId} could not be resolved; the next run tries again.")]
    private static partial void LogPlayerFailed(ILogger logger, Guid userId, Exception exception);
}
