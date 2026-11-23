using Hangfire;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.Jobs;

/// <summary>
/// The single add's moderation at a later moment (design note, "Moderation"): after a confirm, the ticked pieces' cutouts go to the safety filter
/// through <see cref="WardrobeModerator.ModerateAsync"/>, one batch per run. Never at analysis: a piece the user does not add costs no vision call.
/// Until the verdict is <c>Allow</c> the item is not shareable (<see cref="WardrobeModerator.IsShareable"/>), exactly as a single add's first
/// seconds; a failed call leaves the verdict empty and the daily <c>RetryMissingAsync</c> of <c>wardrobe-cleanup</c> asks again. Not gated by
/// <c>BulkAdd:Enabled</c>: items that exist are moderated whatever the flag says.
/// </summary>
internal sealed class BulkAddModerateJob(KombinDbContext db, WardrobeModerator moderator)
{
    public const string JobId = "bulk-add-moderate";

    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("bulk-add-moderate {0}")]
    public async Task RunAsync(Guid batchId, CancellationToken cancellationToken) => await ModerateAsync(batchId, cancellationToken);

    /// <summary>Moderates the confirmed items of the batch that have no verdict yet; returns how many got one.</summary>
    public async Task<int> ModerateAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.BulkBatches.AsNoTracking()
            .Where(candidate => candidate.Id == batchId && candidate.Status == BulkBatchStatus.Confirmed)
            .Select(candidate => new { candidate.UserId, candidate.ConfirmedItemIds })
            .SingleOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            return 0;
        }

        var ids = batch.ConfirmedItemIds;
        var items = await db.WardrobeItems
            .Where(item => item.UserId == batch.UserId && ids.Contains(item.Id) && item.Status == WardrobeItemStatus.Ready && item.ModerationVerdict == null)
            .ToListAsync(cancellationToken);

        var moderated = 0;
        foreach (var item in items)
        {
            if (await moderator.ModerateAsync(item, null, "image/png", cancellationToken) is not null)
            {
                moderated++;
            }
        }

        return moderated;
    }
}
