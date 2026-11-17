using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.Jobs;

/// <summary>What one run deleted.</summary>
internal sealed record LeaguePurgeSummary(int CopiesDeleted, int PairsDeleted, int VoterDaysDeleted, int EntriesDeleted, int OrphansDeleted);

/// <summary>
/// <c>league-purge</c>, daily 04:15 Europe/Istanbul (design note section 13; <c>docs/privacy-notes.md</c> 6b):
/// <list type="number">
/// <item>the image copies of every entry of a day at least <c>League:Retention:ImageDays</c> (7) days old: objects deleted, keys cleared;</item>
/// <item>the pair rows and the voters' day counters of a <b>closed</b> day at least <c>Retention:PairDays</c> (30) days old (the closed results
/// keep what matters; an open day keeps its votes until it is closed);</item>
/// <item>the entries (with their pieces, pairs and reports) of a closed day at least <c>Retention:EntryDays</c> (90) days old; the score rows stay
/// (their <c>entry_id</c> becomes null);</item>
/// <item>every object under <c>league/</c> that no entry piece names and that is older than <c>Retention:OrphanGrace</c> (a day): copies left by a
/// crash between the object write and the commit, or by a delete that failed. Younger ones may belong to a transaction still running.</item>
/// </list>
/// Plain class like the other jobs, idempotent; does nothing while <c>League:Enabled</c> is false. Tests call <see cref="PurgeAsync"/> with
/// their own days (the database and the store are shared) and sweep the orphans only where they planted them.
/// </summary>
internal sealed partial class LeaguePurgeJob(KombinDbContext db, IObjectStorage storage, IClock clock, IOptions<LeagueOptions> options, ILogger<LeaguePurgeJob> logger)
{
    public const string JobId = "league-purge";

    public const string Cron = "15 4 * * *";

    /// <summary>The prefix of every league copy (<c>StorageKeys.Build("league", ...)</c>).</summary>
    public const string CopyPrefix = "league/";

    public const int Batch = 500;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var summary = await PurgeAsync(clock.UtcNow, null, sweepOrphans: true, cancellationToken);
        LogRun(logger, summary.CopiesDeleted, summary.PairsDeleted, summary.VoterDaysDeleted, summary.EntriesDeleted, summary.OrphansDeleted);
    }

    /// <summary><paramref name="days"/> limits the retention steps to those days (tests); null is every day.</summary>
    public async Task<LeaguePurgeSummary> PurgeAsync(DateTimeOffset now, IReadOnlyCollection<DateOnly>? days, bool sweepOrphans, CancellationToken cancellationToken)
    {
        var retention = options.Value.Retention;
        var today = Istanbul.Today(now);
        var imageCutoff = today.AddDays(-retention.ImageDays);
        var pairCutoff = today.AddDays(-retention.PairDays);
        var entryCutoff = today.AddDays(-retention.EntryDays);

        var copies = await DeleteCopiesAsync(imageCutoff, days, cancellationToken);

        var closedDays = db.LeagueDays.Where(row => row.ClosedAt != null).Select(row => row.Day);
        var pairs = db.LeaguePairs.Where(pair => pair.Day <= pairCutoff && closedDays.Contains(pair.Day));
        var voterDays = db.LeagueVoterDays.Where(row => row.Day <= pairCutoff && closedDays.Contains(row.Day));
        var entries = db.LeagueEntries.Where(entry => entry.Day <= entryCutoff && closedDays.Contains(entry.Day));
        if (days is not null)
        {
            pairs = pairs.Where(pair => days.Contains(pair.Day));
            voterDays = voterDays.Where(row => days.Contains(row.Day));
            entries = entries.Where(entry => days.Contains(entry.Day));
        }

        var pairsDeleted = await pairs.ExecuteDeleteAsync(cancellationToken);
        var voterDaysDeleted = await voterDays.ExecuteDeleteAsync(cancellationToken);
        var entriesDeleted = await entries.ExecuteDeleteAsync(cancellationToken);
        var orphans = sweepOrphans ? await DeleteOrphansAsync(now - retention.OrphanGrace, cancellationToken) : 0;

        return new LeaguePurgeSummary(copies, pairsDeleted, voterDaysDeleted, entriesDeleted, orphans);
    }

    /// <summary>The copies of entries of days up to <paramref name="cutoff"/>: objects first, then the keys (a failed delete keeps the keys for the next run).</summary>
    private async Task<int> DeleteCopiesAsync(DateOnly cutoff, IReadOnlyCollection<DateOnly>? days, CancellationToken cancellationToken)
    {
        var old = db.LeagueEntries.Where(entry => entry.Day <= cutoff);
        if (days is not null)
        {
            old = old.Where(entry => days.Contains(entry.Day));
        }

        var count = 0;
        while (true)
        {
            var keys = await db.LeagueEntryPieces.AsNoTracking()
                .Where(piece => piece.ImageKey != null && old.Any(entry => entry.Id == piece.EntryId))
                .OrderBy(piece => piece.ImageKey)
                .Select(piece => piece.ImageKey!)
                .Take(Batch)
                .ToListAsync(cancellationToken);

            if (keys.Count == 0)
            {
                return count;
            }

            await storage.DeleteAsync(keys, cancellationToken);
            await db.LeagueEntryPieces.Where(piece => piece.ImageKey != null && keys.Contains(piece.ImageKey))
                .ExecuteUpdateAsync(setters => setters.SetProperty(piece => piece.ImageKey, (string?)null), cancellationToken);
            count += keys.Count;
        }
    }

    /// <summary>Objects under <see cref="CopyPrefix"/> written before <paramref name="writtenBefore"/> that no entry piece names.</summary>
    private async Task<int> DeleteOrphansAsync(DateTimeOffset writtenBefore, CancellationToken cancellationToken)
    {
        var count = 0;
        var batch = new List<string>();

        await foreach (var stored in storage.ListAsync(CopyPrefix, cancellationToken))
        {
            if (stored.LastModified >= writtenBefore)
            {
                continue;
            }

            batch.Add(stored.Key);
            if (batch.Count == Batch)
            {
                count += await DeleteUnreferencedAsync(batch, cancellationToken);
                batch.Clear();
            }
        }

        return count + await DeleteUnreferencedAsync(batch, cancellationToken);
    }

    private async Task<int> DeleteUnreferencedAsync(List<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return 0;
        }

        var referenced = await db.LeagueEntryPieces.AsNoTracking()
            .Where(piece => piece.ImageKey != null && keys.Contains(piece.ImageKey))
            .Select(piece => piece.ImageKey!)
            .ToListAsync(cancellationToken);
        var orphans = keys.Except(referenced, StringComparer.Ordinal).ToList();
        if (orphans.Count > 0)
        {
            await storage.DeleteAsync(orphans, cancellationToken);
        }

        return orphans.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "league-purge: {Copies} copies, {Pairs} pairs, {VoterDays} voter days, {Entries} entries and {Orphans} orphaned copies deleted.")]
    private static partial void LogRun(ILogger logger, int copies, int pairs, int voterDays, int entries, int orphans);
}
