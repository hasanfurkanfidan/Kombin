using System.Globalization;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Admin;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League;

/// <summary>A reviewer's decision on a reported outfit: put it back (clear its reports) or take it out of the league for good.</summary>
internal enum LeagueReviewDecision
{
    Restore,
    Remove,
}

internal enum LeagueReviewOutcome
{
    Done,

    /// <summary>No such player entry (engine outfits are never reviewed).</summary>
    NotFound,

    /// <summary>A removed entry cannot be restored: removal is final.</summary>
    NotReviewable,
}

/// <summary>What a review did: the entry's status afterwards and whether a closed day's result changed with it.</summary>
internal sealed record LeagueReviewResult(LeagueReviewOutcome Outcome, Guid EntryId, LeagueEntryStatus? Status, bool ResultChanged)
{
    public static LeagueReviewResult Not(LeagueReviewOutcome outcome, Guid entryId) => new(outcome, entryId, null, false);
}

/// <summary>
/// The one home of league review decisions (design note section 13; the under-review rule of task L5), like <c>PostReviewer</c> in Social: the
/// internal endpoint calls it, and an admin page will. In one transaction, holding the entry's day row <c>FOR SHARE</c> (as a vote does), so a
/// review and that day's close never interleave:
/// <list type="bullet">
/// <item><b>Restore</b>: the reports stop counting (<c>counting_reports = 0</c>, <c>reviewed_at</c>), so an open day's outfit is back in the pool;
/// on a closed day the result's <c>under_review</c> mark is removed and its owner sees the rank and the vote points that were stored at the close.</item>
/// <item><b>Remove</b>: the entry is <c>Removed</c> (final; its image copies go with the next <c>league-entries</c> run). On an open day the close
/// then drops every vote on it. On a closed day its result loses the vote points (0, the total recomputed) and the rank, and is marked
/// <c>removed_by_review</c>; the week's live total follows, because it is computed from the daily rows; <b>a week already closed is not undone</b>
/// (its points, ranks, promotions and relegations stay), and <b>no other outfit's rank or points are recomputed</b> (they were decided by the votes
/// of that day; N stays as it was).</item>
/// </list>
/// Removing a removed entry again is a no-op; restoring one is <see cref="LeagueReviewOutcome.NotReviewable"/>. Every decision writes an
/// <c>admin.audit_log</c> row (<c>league.entry.restore</c> / <c>league.entry.remove</c>, target <c>league_entry</c>, no personal data).
/// </summary>
internal sealed class LeagueEntryReviewer(KombinDbContext db, IClock clock, IOptions<LeagueOptions> options)
{
    public const string TargetType = "league_entry";

    public async Task<LeagueReviewResult> ReviewAsync(Guid entryId, LeagueReviewDecision decision, string actor, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var day = await db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Id == entryId && entry.Kind == LeagueEntryKind.Human)
            .Select(entry => (DateOnly?)entry.Day)
            .SingleOrDefaultAsync(cancellationToken);
        if (day is not { } entryDay)
        {
            return LeagueReviewResult.Not(LeagueReviewOutcome.NotFound, entryId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var closesAt = LeagueDays.ClosesAt(entryDay, options.Value.DayClosesAt);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO league.days (day, closes_at, ranked_count, engine_count, pair_count, bt_iterations, bt_converged, close_ms) VALUES ({entryDay}, {closesAt}, 0, 0, 0, 0, false, 0) ON CONFLICT (day) DO NOTHING",
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.days WHERE day = {entryDay} FOR SHARE", cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.entries WHERE id = {entryId} FOR NO KEY UPDATE", cancellationToken);
        var entry = await db.LeagueEntries.SingleAsync(row => row.Id == entryId, cancellationToken);

        if (entry.Status == LeagueEntryStatus.Removed)
        {
            await transaction.CommitAsync(cancellationToken);
            return decision == LeagueReviewDecision.Remove
                ? new LeagueReviewResult(LeagueReviewOutcome.Done, entryId, entry.Status, false)
                : LeagueReviewResult.Not(LeagueReviewOutcome.NotReviewable, entryId);
        }

        var score = entry.UserId is { } owner
            ? await db.LeagueDailyScores.SingleOrDefaultAsync(row => row.UserId == owner && row.Day == entry.Day && row.EntryId == entry.Id, cancellationToken)
            : null;
        var resultChanged = false;

        entry.ReviewedAt = now;
        if (decision == LeagueReviewDecision.Restore)
        {
            entry.CountingReports = 0;
            if (score is { UnderReview: true })
            {
                score.UnderReview = false;
                resultChanged = true;
            }
        }
        else
        {
            entry.Status = LeagueEntryStatus.Removed;
            entry.Reason = null;
            if (score is not null)
            {
                score.Total -= score.VotePoints;
                score.VotePoints = 0;
                score.Ranked = false;
                score.Rank = null;
                score.Percentile = null;
                score.UnderReview = false;
                score.RemovedByReview = true;
                resultChanged = true;
            }
        }

        db.AdminAuditLog.Add(new AdminAuditEntry
        {
            Actor = actor.Length <= AdminAuditEntry.MaxActorLength ? actor : actor[..AdminAuditEntry.MaxActorLength],
            Action = decision == LeagueReviewDecision.Restore ? "league.entry.restore" : "league.entry.remove",
            TargetType = TargetType,
            TargetId = entryId.ToString(),
            Summary = "League day " + entry.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (resultChanged ? "; the closed day's result changed." : "."),
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new LeagueReviewResult(LeagueReviewOutcome.Done, entryId, entry.Status, resultChanged);
    }
}
