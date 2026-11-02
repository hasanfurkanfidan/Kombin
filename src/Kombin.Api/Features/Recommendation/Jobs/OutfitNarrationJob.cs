using Hangfire;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.Jobs;

/// <summary>
/// <c>outfit-narrate</c>: fire-and-forget, enqueued by the outfit endpoints when <c>Narration:Provider = Anthropic</c> and some of the
/// outfits they returned have no cached LLM line. It upgrades the template narration of those outfits in place; the app sees the new
/// line the next time it reads the outfit. Idempotent: outfits already narrated by the LLM are skipped, and a failure leaves the
/// template line (no retry, the next request for a similar outfit tries again).
/// </summary>
internal sealed class OutfitNarrationJob(KombinDbContext db, IOutfitNarrator narrator)
{
    public const string JobId = "outfit-narrate";

    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(Guid[] outfitIds, CancellationToken cancellationToken) => NarrateAsync(outfitIds, cancellationToken);

    /// <summary>Returns how many outfits got an LLM line.</summary>
    public async Task<int> NarrateAsync(IReadOnlyCollection<Guid> outfitIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outfitIds);

        var outfits = await db.Outfits.AsNoTracking()
            .Where(outfit => outfitIds.Contains(outfit.Id) && outfit.NarrationProvider == NarrationProvider.Template)
            .OrderBy(outfit => outfit.Id)
            .Select(outfit => new { outfit.Id, outfit.NarrationInput })
            .ToListAsync(cancellationToken);

        var summaries = outfits
            .Select(outfit => (outfit.Id, Summary: OutfitSummary.FromJson(outfit.NarrationInput)))
            .Where(outfit => outfit.Summary is not null)
            .ToList();

        if (summaries.Count == 0)
        {
            return 0;
        }

        var lines = await narrator.NarrateAsync([.. summaries.Select(outfit => outfit.Summary!)], cancellationToken);
        var upgraded = 0;

        for (var index = 0; index < summaries.Count; index++)
        {
            var line = lines[index];
            if (line.Provider == NarrationProvider.Template)
            {
                continue;
            }

            var id = summaries[index].Id;
            upgraded += await db.Outfits
                .Where(outfit => outfit.Id == id)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(outfit => outfit.Narration, line.Text).SetProperty(outfit => outfit.NarrationProvider, line.Provider),
                    cancellationToken);
        }

        return upgraded;
    }
}
