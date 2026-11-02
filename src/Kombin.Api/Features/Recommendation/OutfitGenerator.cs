using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>One scored combination. <c>Hash</c> identifies its pieces (the accessory excluded: it never decides an outfit).</summary>
internal sealed record OutfitCandidate(IReadOnlyList<OutfitPiece> Pieces, OutfitEvaluation Evaluation, string Hash)
{
    public int Score => Evaluation.Score;

    public OutfitPiece? In(OutfitSlot slot) => Pieces.FirstOrDefault(piece => piece.Slot == slot);

    /// <summary>Catalog pieces other than the anchor.</summary>
    public int GapCount(string? anchorKey) => Pieces.Count(piece => piece.Source == PieceSource.Catalog && piece.Key != anchorKey);

    /// <summary>No gap: complete; one gap: needs one; more: shopping ideas (an outfit needs at least three pieces, so two gaps leave the wardrobe at most one).</summary>
    public OutfitCompleteness Completeness(string? anchorKey) => GapCount(anchorKey) switch
    {
        0 => OutfitCompleteness.Complete,
        1 => OutfitCompleteness.NeedsOne,
        _ => OutfitCompleteness.CatalogOnly,
    };
}

/// <summary>
/// The pieces to combine. <c>Catalog</c> is the narrow prefetch (already filtered by slot, colour family, band, stock, audience and
/// budget). At most <c>MaxGaps</c> catalog pieces (the anchor not counted) go into one outfit.
/// </summary>
internal sealed record GenerationRequest(
    IReadOnlyList<OutfitPiece> Wardrobe,
    IReadOnlyList<OutfitPiece> Catalog,
    OutfitContext Context,
    OutfitPiece? Anchor,
    int MaxGaps,
    IReadOnlySet<string> ExcludedHashes);

internal static class OutfitHash
{
    /// <summary>
    /// SHA-256 of the sorted piece keys as 32 hex characters. Accessories are left out (they never decide an outfit), except the
    /// how-to-wear anchor, which is the point of the outfit.
    /// </summary>
    public static string Of(IEnumerable<OutfitPiece> pieces, string? anchorKey = null) =>
        Of(string.Join('|', pieces.Where(piece => piece.Slot != OutfitSlot.Accessory || piece.Key == anchorKey).Select(piece => piece.Key).Order(StringComparer.Ordinal)));

    public static string Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}

/// <summary>
/// Builds outfits from pieces, without I/O. Pieces are first pruned to the best few per slot (by persona match, weather fit and
/// closeness to the target formality), then every combination is enumerated with early rejection (the hard rules are monotone: a pair
/// that breaks one can never be fixed by adding pieces), scored with <see cref="OutfitScoring"/>, and the ones at or above the
/// acceptance threshold are returned best first.
/// </summary>
internal static class OutfitGenerator
{
    public const int TopsKept = 10;

    public const int BottomsKept = 10;

    public const int ShoesKept = 6;

    public const int OuterwearKept = 5;

    public const int AccessoriesKept = 4;

    /// <summary>Catalog pieces kept per slot after pruning (the prefetch returns more so the pruning has a choice).</summary>
    public const int CatalogKept = 4;

    /// <summary>The daily list every seed indexes into; any two of its outfits differ in at least <see cref="MinDifference"/> pieces.</summary>
    public const int DiverseListSize = 12;

    public const int MinDifference = 2;

    /// <summary>A how-to-wear set uses a companion piece in at most this many outfits.</summary>
    public const int MaxPieceUses = 2;

    /// <summary>The largest re-ranking a seed causes in how-to-wear, in score points: enough to swap near-equals, not to promote a bad outfit.</summary>
    public const double JitterPoints = 6;

    private static readonly Dictionary<OutfitSlot, int> WardrobeKept = new()
    {
        [OutfitSlot.Top] = TopsKept,
        [OutfitSlot.Bottom] = BottomsKept,
        [OutfitSlot.Shoes] = ShoesKept,
        [OutfitSlot.Outerwear] = OuterwearKept,
        [OutfitSlot.Accessory] = AccessoriesKept,
    };

    public static List<OutfitCandidate> Generate(GenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = request.Context;
        var anchorKey = request.Anchor?.Key;

        IReadOnlyList<OutfitPiece?> Options(OutfitSlot slot)
        {
            if (request.Anchor is { } anchor && anchor.Slot == slot)
            {
                return [anchor];
            }

            var owned = Prune(request.Wardrobe.Where(piece => piece.Slot == slot), context, WardrobeKept[slot]);
            var shop = request.MaxGaps > 0 ? Prune(request.Catalog.Where(piece => piece.Slot == slot), context, CatalogKept) : [];

            return [.. owned, .. shop];
        }

        var tops = Options(OutfitSlot.Top);
        var bottoms = Options(OutfitSlot.Bottom);
        var shoes = Options(OutfitSlot.Shoes);
        var outerwear = OuterwearOptions(Options(OutfitSlot.Outerwear), context, request.Anchor);

        // An accessory anchor ("how do I wear this bag?") is in every outfit; other accessories are added after picking.
        var accessory = request.Anchor?.Slot == OutfitSlot.Accessory ? request.Anchor : null;

        var results = new List<OutfitCandidate>();
        var pieces = new List<OutfitPiece>(5);

        foreach (var top in tops.OfType<OutfitPiece>())
        {
            foreach (var bottom in bottoms.OfType<OutfitPiece>())
            {
                if (!Compatible([top, bottom]) || !SilhouetteRules.Judge(top.Fit, bottom.Fit).Allowed)
                {
                    continue;
                }

                foreach (var shoe in shoes.OfType<OutfitPiece>())
                {
                    if (!Compatible([top, bottom, shoe]))
                    {
                        continue;
                    }

                    foreach (var layer in outerwear)
                    {
                        pieces.Clear();
                        pieces.Add(top);
                        pieces.Add(bottom);
                        pieces.Add(shoe);
                        if (layer is not null)
                        {
                            pieces.Add(layer);
                        }

                        if (accessory is not null)
                        {
                            pieces.Add(accessory);
                        }

                        if (pieces.Count(piece => piece.Source == PieceSource.Catalog && piece.Key != anchorKey) > request.MaxGaps)
                        {
                            continue;
                        }

                        var evaluation = OutfitScoring.Evaluate(pieces, context, anchorKey);
                        if (!evaluation.Allowed || evaluation.Score < OutfitScoring.AcceptanceThreshold)
                        {
                            continue;
                        }

                        var hash = OutfitHash.Of(pieces, anchorKey);
                        if (!request.ExcludedHashes.Contains(hash))
                        {
                            results.Add(new OutfitCandidate([.. pieces], evaluation, hash));
                        }
                    }
                }
            }
        }

        results.Sort(static (left, right) => right.Score != left.Score ? right.Score.CompareTo(left.Score) : string.CompareOrdinal(left.Hash, right.Hash));

        return results;
    }

    /// <summary>
    /// The daily pick: <paramref name="seed"/> plus the day indexes the diverse list, so "another one" (the next seed) and tomorrow
    /// (the next day) both move to an outfit that differs in at least two pieces. The two entries after it are the alternatives.
    /// </summary>
    public static IReadOnlyList<OutfitCandidate> PickDaily(IReadOnlyList<OutfitCandidate> ranked, int seed, DateOnly date, int alternatives)
    {
        ArgumentNullException.ThrowIfNull(ranked);

        var diverse = PickDiverse(ranked, DiverseListSize, MinDifference);
        if (diverse.Count == 0)
        {
            return [];
        }

        var start = (int)(((long)seed + date.DayNumber) % diverse.Count);

        return [.. Enumerable.Range(0, Math.Min(diverse.Count, alternatives + 1)).Select(offset => diverse[(start + offset) % diverse.Count])];
    }

    /// <summary>Greedy: best first, keeping an outfit only when it differs from every kept one in at least <paramref name="minDifference"/> pieces.</summary>
    public static List<OutfitCandidate> PickDiverse(IReadOnlyList<OutfitCandidate> ranked, int max, int minDifference)
    {
        ArgumentNullException.ThrowIfNull(ranked);

        var kept = new List<OutfitCandidate>();

        foreach (var candidate in ranked)
        {
            if (kept.All(other => Difference(candidate, other) >= minDifference))
            {
                kept.Add(candidate);
                if (kept.Count == max)
                {
                    break;
                }
            }
        }

        return kept;
    }

    /// <summary>
    /// How-to-wear: up to <paramref name="count"/> outfits, re-ranked by a seeded jitter (seed 0 keeps the plain ranking), no companion
    /// piece in more than <see cref="MaxPieceUses"/> of them. The picks are returned with the complete ones first, then those that need one
    /// piece, then catalog-only ones, whatever their scores: what the user can wear today leads. Within each group the ranking order stays.
    /// </summary>
    public static IReadOnlyList<OutfitCandidate> PickAround(IReadOnlyList<OutfitCandidate> ranked, string anchorKey, int seed, int count)
    {
        ArgumentNullException.ThrowIfNull(ranked);

        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<OutfitCandidate>();

        foreach (var candidate in ranked.OrderByDescending(candidate => candidate.Score + Jitter(seed, candidate.Hash)).ThenBy(candidate => candidate.Hash, StringComparer.Ordinal))
        {
            var companions = candidate.Pieces.Where(piece => piece.Key != anchorKey && piece.Slot != OutfitSlot.Accessory).Select(piece => piece.Key).ToList();
            if (companions.Any(key => uses.GetValueOrDefault(key) >= MaxPieceUses))
            {
                continue;
            }

            companions.ForEach(key => uses[key] = uses.GetValueOrDefault(key) + 1);
            kept.Add(candidate);
            if (kept.Count == count)
            {
                break;
            }
        }

        // OrderBy is stable, so each group keeps its ranking order.
        return [.. kept.OrderBy(candidate => candidate.Completeness(anchorKey))];
    }

    /// <summary>Adds the best wardrobe accessory that keeps the outfit allowed and does not lower its score; otherwise returns it unchanged.</summary>
    public static OutfitCandidate WithAccessory(OutfitCandidate candidate, IReadOnlyList<OutfitPiece> wardrobe, OutfitContext context, string? anchorKey)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(wardrobe);

        if (candidate.In(OutfitSlot.Accessory) is not null)
        {
            return candidate;
        }

        var best = candidate;
        foreach (var accessory in Prune(wardrobe.Where(piece => piece.Slot == OutfitSlot.Accessory), context, AccessoriesKept))
        {
            List<OutfitPiece> pieces = [.. candidate.Pieces, accessory];
            var evaluation = OutfitScoring.Evaluate(pieces, context, anchorKey);

            if (evaluation.Allowed && evaluation.Score >= best.Score)
            {
                best = new OutfitCandidate(pieces, evaluation with { Rule = candidate.Evaluation.Rule }, candidate.Hash);
            }
        }

        return best;
    }

    /// <summary>Slots where the two outfits wear different pieces (top, bottom, shoes, layer; no layer counts as a value).</summary>
    public static int Difference(OutfitCandidate left, OutfitCandidate right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return new[] { OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Shoes, OutfitSlot.Outerwear }
            .Count(slot => left.In(slot)?.Key != right.In(slot)?.Key);
    }

    /// <summary>A deterministic 0..<see cref="JitterPoints"/> from the seed and the outfit (FNV-1a), independent of enumeration order.</summary>
    public static double Jitter(int seed, string hash)
    {
        if (seed == 0)
        {
            return 0;
        }

        var value = 2166136261u;
        foreach (var character in $"{seed}:{hash}")
        {
            value = unchecked((value ^ character) * 16777619u);
        }

        return value % 1000 / 1000.0 * JitterPoints;
    }

    /// <summary>
    /// The best <paramref name="keep"/> pieces of a slot that pass the per-piece rules, by persona match (half), weather fit and closeness
    /// to the target formality, minus a little for pieces just worn; ties by key so the result is deterministic.
    /// </summary>
    public static List<OutfitPiece> Prune(IEnumerable<OutfitPiece> pieces, OutfitContext context, int keep)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. pieces
                .Select(piece => (Piece: piece, Score: PieceScore(piece, context)))
                .Where(scored => scored.Score is not null)
                .OrderByDescending(scored => scored.Score)
                .ThenBy(scored => scored.Piece.Key, StringComparer.Ordinal)
                .Take(keep)
                .Select(scored => scored.Piece),
        ];
    }

    private static double? PieceScore(OutfitPiece piece, OutfitContext context)
    {
        var fit = WeatherRules.PieceFit(piece, context.Weather);

        if (fit is null
            || context.AvoidedColors.Contains(piece.Color)
            || piece.Formality < context.BandMin - OutfitScoring.PieceBandReach
            || piece.Formality > context.BandMax + OutfitScoring.PieceBandReach
            || (piece.Slot == OutfitSlot.Shoes && context.RequiredShoeLeaf is { } shoe && piece.Leaf != shoe))
        {
            return null;
        }

        var closeness = Math.Max(0, 1 - (Math.Abs(piece.Formality - context.FormalityTarget) / 4));
        var worn = piece.LastWornAt is { } day && day <= context.Date && day >= context.Date.AddDays(-OutfitScoring.RecentlyWornDays) ? 0.1 : 0;

        return (0.5 * piece.PersonaScore / 100.0) + (0.3 * fit.Value) + (0.2 * closeness) - worn;
    }

    private static List<OutfitPiece?> OuterwearOptions(IReadOnlyList<OutfitPiece?> layers, OutfitContext context, OutfitPiece? anchor)
    {
        if (anchor?.Slot == OutfitSlot.Outerwear)
        {
            return [anchor];
        }

        return context.Weather.Outerwear switch
        {
            OuterwearPolicy.Forbidden => [null],
            OuterwearPolicy.Required => [.. layers],
            _ => [null, .. layers],
        };
    }

    /// <summary>The monotone hard rules on a partial outfit: if these fail, no added piece can rescue it.</summary>
    private static bool Compatible(OutfitPiece[] pieces) =>
        ColorRules.Judge(pieces).Allowed
        && PatternRules.Judge(pieces).Allowed
        && pieces.Max(piece => piece.Formality) - pieces.Min(piece => piece.Formality) <= FormalityRules.MaxCoreSpread;
}
