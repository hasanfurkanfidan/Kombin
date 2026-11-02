using Hangfire;
using Kombin.Api.Features.Recommendation.Jobs;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Recommendation;

/// <summary>A picked candidate with what it is for the user. <c>AnchorKey</c> is the how-to-wear anchor's piece key.</summary>
internal sealed record OutfitDraft(OutfitCandidate Candidate, string? AnchorKey);

/// <summary>What the request was: the same values on a later request reuse the stored outfits.</summary>
internal sealed record OutfitRequest(
    Guid UserId,
    Guid? PersonaId,
    OutfitKind Kind,
    Occasion? Occasion,
    DateOnly Date,
    int Seed,
    WeatherSnapshot? Weather,
    OutfitContext Context);

/// <summary>Stores suggested outfits and narrates them; the endpoints call it after the engine has picked.</summary>
internal sealed class OutfitStore(KombinDbContext db, IBackgroundJobClient jobs, IOptions<NarrationOptions> narration, TemplateNarrator templates)
{
    /// <summary>
    /// Persists the drafts in order and returns the rows (with items). A draft whose pieces were already suggested for the same user, kind,
    /// day, occasion and persona (and not dismissed) reuses that row, so refreshing the screen does not pile up outfits. The narration is
    /// the template line, or the LLM line cached for the same summary; with <c>Narration:Provider = Anthropic</c> the rest are handed to
    /// the <c>outfit-narrate</c> job.
    /// </summary>
    public async Task<List<Outfit>> PersistAsync(OutfitRequest request, IReadOnlyList<OutfitDraft> drafts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(drafts);

        var hashes = drafts.Select(draft => draft.Candidate.Hash).ToList();
        var existing = await db.Outfits
            .Include(outfit => outfit.Items)
            .Where(outfit => outfit.UserId == request.UserId
                && hashes.Contains(outfit.Hash)
                && outfit.Kind == request.Kind
                && outfit.ForDate == request.Date
                && outfit.Occasion == request.Occasion
                && outfit.PersonaId == request.PersonaId
                && outfit.Status != OutfitStatus.Dismissed)
            .OrderBy(outfit => outfit.CreatedAt)
            .ToListAsync(cancellationToken);

        var summaries = drafts.Select(draft => OutfitSummary.From(draft.Candidate, request.Kind, request.Context, draft.AnchorKey)).ToList();
        var cached = await NarrationCache.LoadAsync(db, [.. summaries.Select(summary => summary.Hash)], now, cancellationToken);
        var lines = await templates.NarrateAsync(summaries, cancellationToken);

        var outfits = new List<Outfit>(drafts.Count);
        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            var summary = summaries[index];
            var outfit = existing.FirstOrDefault(row => row.Hash == draft.Candidate.Hash);

            if (outfit is null)
            {
                outfit = Create(request, draft, summary, lines[index].Text);
                db.Outfits.Add(outfit);
            }

            if (outfit.NarrationProvider == NarrationProvider.Template && cached.TryGetValue(outfit.NarrationHash, out var text))
            {
                outfit.Narration = text;
                outfit.NarrationProvider = NarrationProvider.Anthropic;
            }

            outfits.Add(outfit);
        }

        await db.SaveChangesAsync(cancellationToken);

        var pending = outfits.Where(outfit => outfit.NarrationProvider == NarrationProvider.Template).Select(outfit => outfit.Id).ToArray();
        if (narration.Value.Provider == NarrationProvider.Anthropic && pending.Length > 0)
        {
            jobs.Enqueue<OutfitNarrationJob>(job => job.RunAsync(pending, CancellationToken.None));
        }

        return outfits;
    }

    private static Outfit Create(OutfitRequest request, OutfitDraft draft, OutfitSummary summary, string line)
    {
        var candidate = draft.Candidate;
        var outfit = new Outfit
        {
            UserId = request.UserId,
            PersonaId = request.PersonaId,
            Kind = request.Kind,
            Completeness = candidate.Completeness(draft.AnchorKey),
            Occasion = request.Occasion,
            ForDate = request.Date,
            WeatherJson = OutfitResponses.SerializeWeather(request.Weather),
            Seed = request.Seed,
            Score = candidate.Score,
            Rule = candidate.Evaluation.Rule.ToString(),
            Hash = candidate.Hash,
            Status = OutfitStatus.Suggested,
            Narration = line,
            NarrationProvider = NarrationProvider.Template,
            NarrationHash = summary.Hash,
            NarrationInput = summary.ToJson(),
        };

        var position = 0;
        foreach (var piece in candidate.Pieces.OrderBy(piece => piece.Slot))
        {
            var isAnchor = piece.Key == draft.AnchorKey;
            var fromWardrobe = piece.Source == PieceSource.Wardrobe;

            outfit.Items.Add(new OutfitItem
            {
                Slot = piece.Slot,
                Role = isAnchor ? OutfitItemRole.Anchor : fromWardrobe ? OutfitItemRole.Wardrobe : OutfitItemRole.Gap,
                WardrobeItemId = fromWardrobe ? piece.Id : null,
                ProductId = fromWardrobe ? null : piece.Id,
                OfferId = piece.OfferId,
                Price = piece.Price,
                Position = position++,
            });
        }

        return outfit;
    }
}
