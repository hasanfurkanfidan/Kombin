using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>
/// The cheaper counting path behind <c>possibleOutfits</c> and <c>unlocksEstimate</c>. It counts core outfits (top × bottom × shoes)
/// that pass the outfit rules (colour, formality, silhouette, pattern) and whose rule score, those four components of
/// <see cref="OutfitScoring"/> renormalised, reaches <see cref="OutfitScoring.AcceptanceThreshold"/>. No persona, occasion or weather:
/// the count describes the wardrobe, so it does not change when the user switches persona or the weather turns. Layers and
/// accessories are not counted. Rule verdicts are memoised by the pieces' colour and pattern codes, so a 200-piece wardrobe stays in
/// the low milliseconds; counting stops at <see cref="DisplayCap"/>.
/// </summary>
internal static class OutfitCounter
{
    public const int DisplayCap = 999;

    public static int Count(IReadOnlyList<OutfitPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        var counter = new Memo();

        return counter.CountTriples(
            [.. pieces.Where(piece => piece.Slot == OutfitSlot.Top)],
            [.. pieces.Where(piece => piece.Slot == OutfitSlot.Bottom)],
            [.. pieces.Where(piece => piece.Slot == OutfitSlot.Shoes)],
            extras: []);
    }

    /// <summary>
    /// The outfits <paramref name="added"/> would make possible: core outfits that include it, or for a layer the core outfits it can
    /// be worn over. An accessory unlocks nothing (it is never counted).
    /// </summary>
    public static int CountWith(IReadOnlyList<OutfitPiece> pieces, OutfitPiece added)
    {
        ArgumentNullException.ThrowIfNull(added);

        return added.Slot == OutfitSlot.Accessory ? 0 : CountIncluding(pieces, [added]);
    }

    /// <summary>
    /// The core outfits of <paramref name="pieces"/> that wear all of <paramref name="required"/> (at most one per slot, which replaces what
    /// <paramref name="pieces"/> has in that slot). A layer or an accessory counts the core outfits it can be worn with.
    /// </summary>
    public static int CountIncluding(IReadOnlyList<OutfitPiece> pieces, IReadOnlyList<OutfitPiece> required)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(required);

        List<OutfitPiece> Of(OutfitSlot slot) => required.FirstOrDefault(piece => piece.Slot == slot) is { } fixedPiece ? [fixedPiece] : [.. pieces.Where(piece => piece.Slot == slot)];

        return new Memo().CountTriples(
            Of(OutfitSlot.Top),
            Of(OutfitSlot.Bottom),
            Of(OutfitSlot.Shoes),
            [.. required.Where(piece => piece.Slot is OutfitSlot.Outerwear or OutfitSlot.Accessory)]);
    }

    /// <summary>The rule-only score (0-100) of a core outfit, or null when a rule rejects it. Exposed for the tests.</summary>
    public static int? RuleScore(OutfitPiece top, OutfitPiece bottom, OutfitPiece shoes, OutfitPiece? layer = null) =>
        new Memo().Score(top, bottom, shoes, layer is null ? [] : [layer]);

    private sealed class Memo
    {
        private readonly Dictionary<string, ColorVerdict> _colours = new(StringComparer.Ordinal);

        private readonly Dictionary<string, (bool Allowed, double Score)> _patterns = new(StringComparer.Ordinal);

        public int CountTriples(List<OutfitPiece> tops, List<OutfitPiece> bottoms, List<OutfitPiece> shoes, OutfitPiece[] extras)
        {
            var count = 0;

            foreach (var top in tops)
            {
                foreach (var bottom in bottoms)
                {
                    if (Math.Abs(top.Formality - bottom.Formality) > FormalityRules.MaxCoreSpread
                        || !SilhouetteRules.Judge(top.Fit, bottom.Fit).Allowed
                        || !Colour([top, bottom]).Allowed
                        || !Pattern([top, bottom]).Allowed)
                    {
                        continue;
                    }

                    foreach (var shoe in shoes)
                    {
                        if (Score(top, bottom, shoe, extras) >= OutfitScoring.AcceptanceThreshold && ++count >= DisplayCap)
                        {
                            return DisplayCap;
                        }
                    }
                }
            }

            return count;
        }

        /// <summary><paramref name="extras"/> are the layer and the accessory, if any. The accessory is part of the core, as in <see cref="OutfitScoring"/>.</summary>
        public int? Score(OutfitPiece top, OutfitPiece bottom, OutfitPiece shoes, OutfitPiece[] extras)
        {
            OutfitPiece[] pieces = [top, bottom, shoes, .. extras];

            var layer = extras.FirstOrDefault(piece => piece.Slot == OutfitSlot.Outerwear);
            var formality = FormalityRules.Judge([.. pieces.Where(piece => piece.Slot != OutfitSlot.Outerwear).Select(piece => piece.Formality)], layer?.Formality);
            var silhouette = SilhouetteRules.Judge(top.Fit, bottom.Fit);
            var colour = Colour(pieces);
            var pattern = Pattern(pieces);

            if (!formality.Allowed || !silhouette.Allowed || !colour.Allowed || !pattern.Allowed)
            {
                return null;
            }

            return OutfitScoring.RuleScore(colour.Score, formality.Score, silhouette.Score, pattern.Score);
        }

        // A colour verdict depends only on each piece's colour and whether it counts as neutral; a pattern verdict only on each piece's
        // pattern strength and scale. The sorted codes are the memo keys.
        private ColorVerdict Colour(OutfitPiece[] pieces)
        {
            var key = string.Join(',', pieces.Select(piece => $"{(int)piece.Color}{(ColorRules.IsNeutral(piece) ? 'n' : 'c')}").Order(StringComparer.Ordinal));
            if (!_colours.TryGetValue(key, out var verdict))
            {
                verdict = ColorRules.Judge(pieces);
                _colours[key] = verdict;
            }

            return verdict;
        }

        private (bool Allowed, double Score) Pattern(OutfitPiece[] pieces)
        {
            var key = string.Join(',', pieces.Select(piece => PatternRules.Strong.Contains(piece.Pattern) ? PatternRules.IsSmallScale(piece) ? "s" : "b" : "-").Order(StringComparer.Ordinal));
            if (!_patterns.TryGetValue(key, out var verdict))
            {
                verdict = PatternRules.Judge(pieces);
                _patterns[key] = verdict;
            }

            return verdict;
        }
    }
}
