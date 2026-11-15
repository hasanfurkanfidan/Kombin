using Kombin.Api.Features.Profile;

namespace Kombin.Api.Features.Recommendation;

// BUDGET SEARCH - which outfits a budget buys. No rules of its own about what looks good: the engine enumerates (OutfitGenerator) and
// scores (OutfitScoring); this file decides which catalog pieces the engine sees and which of its outfits are shown. Design:
// docs/design/budget-and-trips.md, section 1. Change a constant here, in its comment and in the doc together.
//
//   cost(outfit)  = the sum of its catalog pieces' prices (each the cheapest in-size offer, price plus that seller's shipping); owned
//                   pieces cost 0.
//   fits          = cost <= budget, score >= OutfitScoring.AcceptanceThreshold (the engine's floor, never lowered), and at least one
//                   piece to buy (without one it is the daily outfit).
//   order         = score, then more owned pieces, then lower cost, then the outfit hash (so equal inputs give equal answers).
//   diverse       = two shown outfits share at most half of either one's bought pieces: 2·|B1 ∩ B2| <= min(|B1|, |B2|).
//   shown         = up to DiverseListSize outfits kept greedily in order; seed s shows the window [s·count, s·count + count) of that list,
//                   wrapping, so the next seed is "other suggestions" and every one of them fits.
//   over budget   = only when nothing fits: the cheapest acceptable outfit costing at most OverBudgetReach × budget (higher score on ties),
//                   alone, with what it costs above the budget.
//
//   Shortlist (per slot, before enumerating): catalog pieces that pass the context's per-piece rules (OutfitGenerator.PieceScore) are
//   sorted by price and cut into Quartiles of equal size; the best QuartilePicks (LayerQuartilePicks for the layer) of each quartile by piece
//   score survive. Cheap and expensive pieces both reach the engine, which is the point: the best outfit for 800 ₺ and for 8,000 ₺ are made
//   of different pieces.

/// <summary>An outfit the budget search kept: the engine's candidate with what it costs and how much of it the user owns.</summary>
internal sealed record BudgetOutfit(OutfitCandidate Candidate, decimal TotalPrice, int OwnedCount, int BoughtCount)
{
    public IEnumerable<string> BoughtKeys => Candidate.Pieces.Where(piece => piece.Source == PieceSource.Catalog).Select(piece => piece.Key);

    public static BudgetOutfit From(OutfitCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var core = candidate.Pieces;

        return new BudgetOutfit(
            candidate,
            core.Sum(piece => OutfitGenerator.Cost(piece, anchorKey: null)),
            core.Count(piece => piece.Source == PieceSource.Wardrobe),
            core.Count(piece => piece.Source == PieceSource.Catalog));
    }
}

/// <summary><c>Outfits</c> fit the budget; <c>Closest</c> is set only when none does (the one honest over-budget answer).</summary>
internal sealed record BudgetPick(IReadOnlyList<BudgetOutfit> Outfits, BudgetOutfit? Closest);

internal static class BudgetSearch
{
    /// <summary>The over-budget answer may cost at most this many times the budget; the candidate query's price cap is the same.</summary>
    public const decimal OverBudgetReach = 1.5m;

    public const int Quartiles = 4;

    public const int QuartilePicks = 3;

    public const int LayerQuartilePicks = 2;

    /// <summary>Catalog pieces per slot that reach the engine: the shortlist's largest possible size.</summary>
    public const int CatalogPerSlot = Quartiles * QuartilePicks;

    /// <summary>Every slot may be bought (a budget outfit without the wardrobe is all catalog).</summary>
    public const int MaxGaps = 4;

    public const int DiverseListSize = 12;

    public const int MaxCount = 5;

    public static decimal Reach(decimal budget) => budget * OverBudgetReach;

    /// <summary>The catalog pieces of one slot that reach the engine: the best by piece score in each price quartile (see the comment block).</summary>
    public static List<OutfitPiece> Shortlist(IEnumerable<OutfitPiece> slotPieces, OutfitContext context)
    {
        ArgumentNullException.ThrowIfNull(slotPieces);
        ArgumentNullException.ThrowIfNull(context);

        var usable = slotPieces
            .Select(piece => (Piece: piece, Score: OutfitGenerator.PieceScore(piece, context)))
            .Where(scored => scored.Score is not null)
            .OrderBy(scored => scored.Piece.Price ?? 0)
            .ThenBy(scored => scored.Piece.Key, StringComparer.Ordinal)
            .ToList();

        if (usable.Count == 0)
        {
            return [];
        }

        var picks = usable[0].Piece.Slot == Persistence.Entities.Recommendation.OutfitSlot.Outerwear ? LayerQuartilePicks : QuartilePicks;
        var kept = new List<OutfitPiece>();

        for (var quartile = 0; quartile < Quartiles; quartile++)
        {
            var from = usable.Count * quartile / Quartiles;
            var to = usable.Count * (quartile + 1) / Quartiles;

            kept.AddRange(usable[from..to]
                .OrderByDescending(scored => scored.Score)
                .ThenBy(scored => scored.Piece.Key, StringComparer.Ordinal)
                .Take(picks)
                .Select(scored => scored.Piece));
        }

        return kept;
    }

    /// <summary>
    /// The outfits to show for <paramref name="budget"/> from the engine's acceptable outfits (any order). <paramref name="count"/> at most, the
    /// window chosen by <paramref name="seed"/>; or, when none fits, the closest one over the budget within <see cref="OverBudgetReach"/>.
    /// </summary>
    public static BudgetPick Pick(IReadOnlyList<OutfitCandidate> acceptable, decimal budget, int count, int seed)
    {
        ArgumentNullException.ThrowIfNull(acceptable);

        var buying = acceptable
            .Where(candidate => candidate.Score >= OutfitScoring.AcceptanceThreshold)
            .Select(BudgetOutfit.From)
            .Where(outfit => outfit.BoughtCount > 0)
            .ToList();

        var fitting = buying.Where(outfit => outfit.TotalPrice <= budget).OrderBy(outfit => outfit, Order).ToList();
        var diverse = new List<BudgetOutfit>();

        foreach (var outfit in fitting)
        {
            if (diverse.All(kept => Diverse(kept, outfit)))
            {
                diverse.Add(outfit);
                if (diverse.Count == DiverseListSize)
                {
                    break;
                }
            }
        }

        if (diverse.Count > 0)
        {
            var shown = Math.Clamp(count, 1, MaxCount);
            var start = (int)((long)Math.Max(0, seed) * shown % diverse.Count);

            return new BudgetPick([.. Enumerable.Range(0, Math.Min(shown, diverse.Count)).Select(offset => diverse[(start + offset) % diverse.Count])], null);
        }

        var reach = Reach(budget);
        var closest = buying
            .Where(outfit => outfit.TotalPrice <= reach)
            .OrderBy(outfit => outfit.TotalPrice)
            .ThenByDescending(outfit => outfit.Candidate.Score)
            .ThenBy(outfit => outfit.Candidate.Hash, StringComparer.Ordinal)
            .FirstOrDefault();

        return new BudgetPick([], closest);
    }

    /// <summary>Two outfits share at most half of either one's bought pieces.</summary>
    public static bool Diverse(BudgetOutfit left, BudgetOutfit right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Candidate.Hash == right.Candidate.Hash)
        {
            return false;
        }

        var shared = left.BoughtKeys.Intersect(right.BoughtKeys, StringComparer.Ordinal).Count();

        return 2 * shared <= Math.Min(left.BoughtCount, right.BoughtCount);
    }

    /// <summary>Score, then more owned pieces, then the lower total, then the hash.</summary>
    public static readonly IComparer<BudgetOutfit> Order = Comparer<BudgetOutfit>.Create((left, right) =>
    {
        var byScore = right.Candidate.Score.CompareTo(left.Candidate.Score);
        if (byScore != 0)
        {
            return byScore;
        }

        var byOwned = right.OwnedCount.CompareTo(left.OwnedCount);
        if (byOwned != 0)
        {
            return byOwned;
        }

        var byPrice = left.TotalPrice.CompareTo(right.TotalPrice);

        return byPrice != 0 ? byPrice : string.CompareOrdinal(left.Candidate.Hash, right.Candidate.Hash);
    });
}

/// <summary>A seller's listing of a product as the size choice sees it. <c>Sizes</c> empty: the seller did not say.</summary>
internal sealed record OfferOption(Guid OfferId, decimal EffectivePrice, decimal CurrentPrice, decimal? OriginalPrice, string SellerName, IReadOnlyList<string> Sizes);

/// <summary>
/// The offer a budget outfit buys. <c>Size</c> is the caller's size when the offer lists it; <c>SizeUnknown</c> when the caller's size is not
/// known or the offer does not list sizes. <c>Savings</c> exists only when the seller shows a real crossed-out price above today's.
/// </summary>
internal sealed record BudgetOffer(Guid OfferId, decimal Price, decimal CurrentPrice, decimal? OriginalPrice, string? Size, bool SizeUnknown)
{
    public decimal? Savings => OriginalPrice is { } original && original > CurrentPrice ? original - CurrentPrice : null;
}

internal static class BudgetOffers
{
    /// <summary>
    /// The cheapest offer by effective price (then price, then seller name, as the product page orders them) in <paramref name="size"/>:
    /// offers listing it first; else, offers listing no sizes at all (size unknown); offers listing other sizes only never. Without a size,
    /// the cheapest offer, size unknown. Null when nothing qualifies.
    /// </summary>
    public static BudgetOffer? Choose(IReadOnlyCollection<OfferOption> offers, string? size)
    {
        ArgumentNullException.ThrowIfNull(offers);

        var ordered = offers
            .OrderBy(offer => offer.EffectivePrice)
            .ThenBy(offer => offer.CurrentPrice)
            .ThenBy(offer => offer.SellerName, StringComparer.Ordinal)
            .ThenBy(offer => offer.OfferId)
            .ToList();

        if (size is null)
        {
            return ordered.FirstOrDefault() is { } any ? Build(any, null) : null;
        }

        var wanted = SizeGroups.NormalizeSize(size);
        if (ordered.FirstOrDefault(offer => offer.Sizes.Any(listed => SizeGroups.NormalizeSize(listed) == wanted)) is { } inSize)
        {
            return Build(inSize, wanted);
        }

        return ordered.FirstOrDefault(offer => offer.Sizes.Count == 0) is { } unlisted ? Build(unlisted, null) : null;
    }

    /// <summary>
    /// The size the caller wears for <paramref name="brandId"/> in a group: their own entry for that brand, else the inferred one
    /// (<see cref="SizeInference"/>: two other brands agreeing), else null. <paramref name="entries"/> are the caller's entries of that group.
    /// </summary>
    public static string? SizeFor(IReadOnlyCollection<(Guid BrandId, string Size, DateTimeOffset UpdatedAt)> entries, Guid brandId)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
        {
            if (entry.BrandId == brandId)
            {
                return entry.Size;
            }
        }

        return SizeInference.Suggest([.. entries.Select(entry => new SizeVote(entry.Size, entry.UpdatedAt))])?.Size;
    }

    private static BudgetOffer Build(OfferOption offer, string? size) =>
        new(offer.OfferId, offer.EffectivePrice, offer.CurrentPrice, offer.OriginalPrice, size, size is null);
}
