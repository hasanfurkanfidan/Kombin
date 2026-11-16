using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>One engine outfit from the catalog: its pieces in slot order, each with the product's primary image, and the engine's score (≥ 60).</summary>
internal sealed record SampledOutfit(IReadOnlyList<(OutfitSlot Slot, Guid ProductId, string ImageUrl)> Pieces, int Score);

/// <summary>
/// Catalog-only outfits for Kombin Ligi's small pools ("az oyunculu günlerde havuzu öneri motorunun kombinleri doldurur"; design:
/// <c>docs/design/league.md</c>, section 6). The seam the league calls (the league never copies the engine): the outfits come from
/// <see cref="OutfitGenerator.Generate"/> through the narrow per-slot prefetch (<see cref="OutfitSources.LoadCatalogAsync"/>: slot categories,
/// the formality band, active with a sellable offer, the audience, newest first), score at or above the acceptance floor like every engine
/// outfit, and are spread with <see cref="OutfitGenerator.PickDiverse"/> after a seeded jitter, so the same day and seed give the same list.
/// Catalog-only on purpose: an engine outfit built from someone's wardrobe would show their pieces to strangers. No persona, no weather
/// forecast (the date's season), no wardrobe; an outfit with a piece without an image is left out.
/// </summary>
internal static class CatalogOutfitSampler
{
    /// <summary>Catalog pieces per slot that survive the pruning: more than a shopping idea keeps, so a pool of 30 finds enough variety.</summary>
    public const int CatalogKept = 8;

    /// <summary>Catalog pieces per outfit: an engine outfit is all catalog.</summary>
    public const int MaxGaps = 4;

    public static async Task<IReadOnlyList<SampledOutfit>> SampleAsync(
        KombinDbContext db, Audience audience, Occasion? occasion, DateOnly date, int count, int seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (count <= 0)
        {
            return [];
        }

        var context = OutfitContext.Create(occasion, PersonaTaste.None, WeatherRules.FromSeason(date), date);
        var slots = context.Weather.Outerwear == OuterwearPolicy.Forbidden ? OutfitSlots.Core : [.. OutfitSlots.Core, OutfitSlot.Outerwear];
        var colours = Enum.GetValues<ProductColor>();
        var catalog = await OutfitSources.LoadCatalogAsync(
            db,
            slots.Select(slot => new CatalogQuery(slot, colours, context.BandMin, context.BandMax, audience, null, null, [])),
            null,
            cancellationToken);

        var ranked = OutfitGenerator.Generate(new GenerationRequest([], catalog, context, null, MaxGaps, new HashSet<string>(StringComparer.Ordinal), CatalogKept))
            .OrderByDescending(candidate => candidate.Score + OutfitGenerator.Jitter(seed, candidate.Hash))
            .ThenBy(candidate => candidate.Hash, StringComparer.Ordinal)
            .ToList();

        var productIds = ranked.SelectMany(candidate => candidate.Pieces).Select(piece => piece.Id).Distinct().ToList();
        var images = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id) && product.PrimaryImageUrl != null)
            .ToDictionaryAsync(product => product.Id, product => product.PrimaryImageUrl!, cancellationToken);

        return
        [
            .. OutfitGenerator.PickDiverse([.. ranked.Where(candidate => candidate.Pieces.All(piece => images.ContainsKey(piece.Id)))], count, OutfitGenerator.MinDifference)
                .Select(candidate => new SampledOutfit(
                    [.. candidate.Pieces.OrderBy(piece => piece.Slot).Select(piece => (piece.Slot, piece.Id, images[piece.Id]))],
                    candidate.Score)),
        ];
    }
}
