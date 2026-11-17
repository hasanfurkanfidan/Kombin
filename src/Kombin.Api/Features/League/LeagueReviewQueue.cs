using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League;

/// <summary>
/// One reported outfit waiting for a review: its entry ID (operators only; never shown to players), its day and status, the reports that count
/// toward the hide and every report since the last review, how many of those reports gave each reason (<see cref="LeagueReportReason"/> names, or
/// <see cref="LeagueReviewQueue.NoReason"/> for a report without one), whether a closed day's result is held back
/// for it (<c>UnderReview</c>), and the pieces as voters see them. Nothing about the owner or the reporters.
/// </summary>
internal sealed record LeagueReviewQueueItem(
    Guid EntryId,
    DateOnly Day,
    LeagueEntryStatus Status,
    int CountingReports,
    int Reports,
    IReadOnlyDictionary<string, int> Reasons,
    bool UnderReview,
    IReadOnlyList<LeaguePieceResponse> Pieces);

/// <summary>
/// The league's review queue, in one place for its two readers: <c>GET /internal/league/review-queue</c> (workers) and the founder's
/// <c>/admin/league</c> page, a documented seam like <see cref="LeagueEntryReviewer"/> (the decision side). Every player outfit with a report since
/// its last review that is not removed (design note section 13: every reported outfit is here, hidden or not), the oldest such report first.
/// </summary>
internal static class LeagueReviewQueue
{
    /// <summary>At most this many outfits per call.</summary>
    public const int Limit = 100;

    /// <summary>The key of the reports that gave no reason (the reason is optional).</summary>
    public const string NoReason = "NoReason";

    public static async Task<IReadOnlyList<LeagueReviewQueueItem>> ListAsync(KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(storage);

        var items = await db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Kind == LeagueEntryKind.Human && entry.Status != LeagueEntryStatus.Removed)
            .Select(entry => new
            {
                entry.Id,
                entry.Day,
                entry.Status,
                entry.CountingReports,
                entry.ReviewedAt,
                Reports = db.LeagueReports.Count(report => report.EntryId == entry.Id && (entry.ReviewedAt == null || report.CreatedAt > entry.ReviewedAt)),
                First = db.LeagueReports
                    .Where(report => report.EntryId == entry.Id && (entry.ReviewedAt == null || report.CreatedAt > entry.ReviewedAt))
                    .Min(report => (DateTimeOffset?)report.CreatedAt),
                UnderReview = db.LeagueDailyScores.Any(score => score.EntryId == entry.Id && score.UnderReview),
            })
            .Where(entry => entry.Reports > 0)
            .OrderBy(entry => entry.First)
            .ThenBy(entry => entry.Id)
            .Take(Limit)
            .ToListAsync(cancellationToken);

        var ids = items.Select(item => item.Id).ToList();
        var pieces = await LeagueResponses.ForVotersAsync(db, storage, ids, cancellationToken);

        // The reasons of the reports since each outfit's last review (the same set the count above is over); only the reason, never the reporter.
        var reports = (await db.LeagueReports.AsNoTracking()
                .Where(report => ids.Contains(report.EntryId))
                .Select(report => new { report.EntryId, report.Reason, report.CreatedAt })
                .ToListAsync(cancellationToken))
            .ToLookup(report => report.EntryId);

        return
        [
            .. items.Select(item => new LeagueReviewQueueItem(
                item.Id,
                item.Day,
                item.Status,
                item.CountingReports,
                item.Reports,
                reports[item.Id]
                    .Where(report => item.ReviewedAt is null || report.CreatedAt > item.ReviewedAt)
                    .GroupBy(report => report.Reason?.ToString() ?? NoReason)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count()),
                item.UnderReview,
                pieces[item.Id])),
        ];
    }
}
