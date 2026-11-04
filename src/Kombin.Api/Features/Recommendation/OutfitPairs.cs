using System.Text.Json.Serialization;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

// PAIRS - "what in my wardrobe goes with this piece". No rule of its own: it asks the outfit rules about two pieces instead of a whole
// outfit, so a pair the engine would never put together is never shown. Read by the mobile team.
//
//   Two pieces pair when they are worn in different slots (two tops or two layers are alternatives, not a pair) and ColorRules,
//   FormalityRules, PatternRules and, for a top and a bottom, SilhouetteRules allow them. Avoided colours, weather and occasions play no
//   part: a pair describes the clothes, so companionCount does not change with the persona or the weather.
//
//   score    OutfitScoring.RuleScore of the colour, formality, silhouette and pattern scores. Only a top and a bottom have a silhouette to
//            judge; every other pair takes the table's neutral SilhouetteRules.UnknownScore.
//            With a persona: OutfitScoring.WithPersona, which mixes in the companion's MatchScorer score. It reorders, it never excludes.
//   reason   the colour harmony in one word: neutral (all neutrals, quiet pairs included), tonal, complementary, analogous, statement.
//
//   unlock   the one catalog piece worth suggesting. Slots: top, bottom, shoes and outerwear, without the item's own; an accessory unlocks
//            nothing (OutfitCounter never counts one). Those with fewer than EnoughCompanions companions are tried fewest first, ties in that
//            order. A candidate must pair with the item and, with a persona, reach the minimum match. Neutral colours win, then the
//            most outfits gained, then the persona's match, then the price.
//   unlocksEstimate   OutfitCounter.CountIncluding of the item and the candidate: every outfit wearing both is one the item gains.

/// <summary>Why two pieces go together, in the word clients read.</summary>
internal enum PairReason
{
    [JsonStringEnumMemberName("neutral")]
    Neutral,

    [JsonStringEnumMemberName("tonal")]
    Tonal,

    [JsonStringEnumMemberName("complementary")]
    Complementary,

    [JsonStringEnumMemberName("analogous")]
    Analogous,

    [JsonStringEnumMemberName("statement")]
    Statement,
}

/// <summary>Allowed (with a 0-100 score and the reason) or rejected.</summary>
internal readonly record struct PairVerdict(bool Allowed, int Score, PairReason Reason)
{
    public static readonly PairVerdict Rejected = new(false, 0, PairReason.Neutral);
}

internal sealed record PairCompanion(OutfitPiece Piece, int Score, PairReason Reason);

/// <summary><c>Total</c> counts every companion in the slot; <c>Companions</c> holds the best of them, best first.</summary>
internal sealed record PairGroup(OutfitSlot Slot, int Total, IReadOnlyList<PairCompanion> Companions);

internal sealed record PairUnlock(OutfitPiece Piece, int UnlocksEstimate);

internal static class OutfitPairs
{
    /// <summary>A slot with this many companions gives the item enough variety: no suggestion needed.</summary>
    public const int EnoughCompanions = 3;

    private static readonly OutfitSlot[] Slots = [OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Outerwear, OutfitSlot.Shoes, OutfitSlot.Accessory];

    private static readonly OutfitSlot[] UnlockSlotOrder = [.. OutfitSlots.Core, OutfitSlot.Outerwear];

    public static PairVerdict Judge(OutfitPiece first, OutfitPiece second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Slot == second.Slot)
        {
            return PairVerdict.Rejected;
        }

        OutfitPiece[] pieces = [first, second];

        var layer = pieces.FirstOrDefault(piece => piece.Slot == OutfitSlot.Outerwear);
        var formality = FormalityRules.Judge([.. pieces.Where(piece => piece.Slot != OutfitSlot.Outerwear).Select(piece => piece.Formality)], layer?.Formality);
        var colour = ColorRules.Judge(pieces);
        var silhouette = Silhouette(first, second);
        var pattern = PatternRules.Judge(pieces);

        if (!formality.Allowed || !colour.Allowed || !silhouette.Allowed || !pattern.Allowed)
        {
            return PairVerdict.Rejected;
        }

        return new PairVerdict(true, OutfitScoring.RuleScore(colour.Score, formality.Score, silhouette.Score, pattern.Score), ReasonOf(colour.Harmony));
    }

    /// <summary>
    /// The pieces of <paramref name="wardrobe"/> that pair with <paramref name="item"/>, one group per slot other than its own (empty ones
    /// included, in a fixed order), at most <paramref name="limit"/> per slot. <paramref name="weighPersona"/> mixes in the pieces' persona score.
    /// </summary>
    public static List<PairGroup> Companions(OutfitPiece item, IReadOnlyList<OutfitPiece> wardrobe, int limit, bool weighPersona)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(wardrobe);

        var companions = wardrobe
            .Select(piece => (Piece: piece, Verdict: Judge(item, piece)))
            .Where(pair => pair.Verdict.Allowed)
            .Select(pair => new PairCompanion(
                pair.Piece,
                weighPersona ? OutfitScoring.WithPersona(pair.Verdict.Score, pair.Piece.PersonaScore) : pair.Verdict.Score,
                pair.Verdict.Reason))
            .OrderByDescending(companion => companion.Score)
            .ThenBy(companion => companion.Piece.Key, StringComparer.Ordinal)
            .ToList();

        return
        [
            .. Slots
                .Where(slot => slot != item.Slot)
                .Select(slot =>
                {
                    var inSlot = companions.Where(companion => companion.Piece.Slot == slot).ToList();

                    return new PairGroup(slot, inSlot.Count, [.. inSlot.Take(limit)]);
                }),
        ];
    }

    /// <summary>How many companions each piece has, by piece ID. Each pair is judged once.</summary>
    public static Dictionary<Guid, int> CountCompanions(IReadOnlyList<OutfitPiece> wardrobe)
    {
        ArgumentNullException.ThrowIfNull(wardrobe);

        var counts = wardrobe.ToDictionary(piece => piece.Id, _ => 0);

        for (var first = 0; first < wardrobe.Count; first++)
        {
            for (var second = first + 1; second < wardrobe.Count; second++)
            {
                if (Judge(wardrobe[first], wardrobe[second]).Allowed)
                {
                    counts[wardrobe[first].Id]++;
                    counts[wardrobe[second].Id]++;
                }
            }
        }

        return counts;
    }

    /// <summary>The slots worth a suggestion, emptiest first. Empty when every one of them already has enough companions.</summary>
    public static List<OutfitSlot> UnlockSlots(OutfitPiece item, IReadOnlyList<PairGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(groups);

        // OrderBy is stable, so equally empty slots keep the order of UnlockSlotOrder: the core before the layer.
        return
        [
            .. UnlockSlotOrder
                .Where(slot => slot != item.Slot)
                .Select(slot => (Slot: slot, groups.Single(group => group.Slot == slot).Total))
                .Where(slot => slot.Total < EnoughCompanions)
                .OrderBy(slot => slot.Total)
                .Select(slot => slot.Slot),
        ];
    }

    /// <summary>The best of <paramref name="catalog"/> for the item, or null when none pairs with it. A null <paramref name="minMatchScore"/> skips the persona's check.</summary>
    public static PairUnlock? PickUnlock(OutfitPiece item, IReadOnlyList<OutfitPiece> wardrobe, IEnumerable<OutfitPiece> catalog, int? minMatchScore)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog
            .Where(piece => (minMatchScore is null || piece.PersonaScore >= minMatchScore) && Judge(item, piece).Allowed)
            .Select(piece => new PairUnlock(piece, OutfitCounter.CountIncluding(wardrobe, [item, piece])))
            .OrderByDescending(unlock => ColorRules.IsNeutral(unlock.Piece))
            .ThenByDescending(unlock => unlock.UnlocksEstimate)
            .ThenByDescending(unlock => unlock.Piece.PersonaScore)
            .ThenBy(unlock => unlock.Piece.Price)
            .ThenBy(unlock => unlock.Piece.Key, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static (bool Allowed, double Score) Silhouette(OutfitPiece first, OutfitPiece second) => (first.Slot, second.Slot) switch
    {
        (OutfitSlot.Top, OutfitSlot.Bottom) => SilhouetteRules.Judge(first.Fit, second.Fit),
        (OutfitSlot.Bottom, OutfitSlot.Top) => SilhouetteRules.Judge(second.Fit, first.Fit),
        _ => (true, SilhouetteRules.UnknownScore),
    };

    private static PairReason ReasonOf(ColorHarmony harmony) => harmony switch
    {
        ColorHarmony.Monochrome => PairReason.Tonal,
        ColorHarmony.Complementary => PairReason.Complementary,
        ColorHarmony.Analogous => PairReason.Analogous,
        ColorHarmony.OneStatement => PairReason.Statement,
        _ => PairReason.Neutral,
    };
}
