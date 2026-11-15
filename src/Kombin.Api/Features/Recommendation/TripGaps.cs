using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>Why a day of a trip has no outfit, as far as one slot is concerned.</summary>
internal enum TripGapReason
{
    /// <summary>The wardrobe has nothing in that slot.</summary>
    Missing,

    /// <summary>It has pieces there, but the day's weather rules them all out (or asks for a layer none of them is).</summary>
    Weather,

    /// <summary>It has pieces there, but they are all too formal or too casual for the day's plan (or in an avoided colour).</summary>
    Occasion,

    /// <summary>Every slot has usable pieces, yet no combination scores well enough.</summary>
    NoCombination,

    /// <summary>Outfits exist, but the wear rules (no top two days running, at most two wears, ...) ran out of pieces.</summary>
    Repetition,
}

/// <summary>One missing piece: its slot, why, and the trip days (indexes) it would dress.</summary>
internal sealed record TripGap(OutfitSlot Slot, TripGapReason Reason, IReadOnlyList<int> Days);

/// <summary>
/// The gaps of a trip plan, pure. For every day without an outfit: every core slot (top, bottom, shoes), and the layer when the weather
/// requires one, with no usable piece is a gap (<see cref="TripGapReason.Missing"/>, <see cref="TripGapReason.Weather"/> or
/// <see cref="TripGapReason.Occasion"/>); with usable pieces everywhere it is <see cref="TripGapReason.NoCombination"/> (the slot with the fewest
/// usable pieces) when the day had no acceptable outfit at all, else <see cref="TripGapReason.Repetition"/> (the first of top, bottom, shoes whose
/// usable pieces are all used up by the wear rules; the top when none is). One gap per (slot, reason), with all its days.
/// </summary>
internal static class TripGaps
{
    private static readonly OutfitSlot[] Checked = [OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Shoes];

    public static List<TripGap> Analyze(
        IReadOnlyList<TripDay> days,
        IReadOnlyList<OutfitPiece> wardrobe,
        IReadOnlyList<IReadOnlyList<OutfitCandidate>> options,
        IReadOnlyList<OutfitCandidate?> outfits,
        IReadOnlyCollection<int> droppedDays,
        TripOptions tripOptions)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(wardrobe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outfits);
        ArgumentNullException.ThrowIfNull(droppedDays);

        var found = new List<(OutfitSlot Slot, TripGapReason Reason, int Day)>();

        for (var day = 0; day < days.Count; day++)
        {
            // A day dropped to keep within the piece limit is the limit's doing, not a missing piece.
            if (outfits[day] is not null || droppedDays.Contains(day))
            {
                continue;
            }

            var context = days[day].Context;
            var slots = context.Weather.Outerwear == OuterwearPolicy.Required ? [.. Checked, OutfitSlot.Outerwear] : Checked;
            var usable = slots.ToDictionary(slot => slot, slot => wardrobe.Where(piece => piece.Slot == slot && OutfitGenerator.PieceScore(piece, context) is not null).ToList());

            var empty = slots
                .Select(slot => (Slot: slot, Reason: SlotReason(slot, wardrobe, usable[slot], context)))
                .Where(entry => entry.Reason is not null)
                .ToList();

            if (empty.Count > 0)
            {
                found.AddRange(empty.Select(entry => (entry.Slot, entry.Reason!.Value, day)));
            }
            else if (options[day].Count == 0)
            {
                found.Add((slots.OrderBy(slot => usable[slot].Count).First(), TripGapReason.NoCombination, day));
            }
            else
            {
                found.Add((ExhaustedSlot(day, usable, outfits, tripOptions), TripGapReason.Repetition, day));
            }
        }

        return
        [
            .. found
                .GroupBy(entry => (entry.Slot, entry.Reason))
                .OrderBy(group => group.Min(entry => entry.Day))
                .Select(group => new TripGap(group.Key.Slot, group.Key.Reason, [.. group.Select(entry => entry.Day).Order()])),
        ];
    }

    private static TripGapReason? SlotReason(OutfitSlot slot, IReadOnlyList<OutfitPiece> wardrobe, List<OutfitPiece> usable, OutfitContext context)
    {
        if (usable.Count > 0)
        {
            return null;
        }

        var inSlot = wardrobe.Where(piece => piece.Slot == slot).ToList();
        if (inSlot.Count == 0)
        {
            return TripGapReason.Missing;
        }

        return inSlot.Any(piece => WeatherRules.PieceFit(piece, context.Weather) is not null) ? TripGapReason.Occasion : TripGapReason.Weather;
    }

    /// <summary>The first of top, bottom and shoes whose usable pieces are all at their cap or worn on a neighbouring day; the top otherwise.</summary>
    private static OutfitSlot ExhaustedSlot(
        int day, Dictionary<OutfitSlot, List<OutfitPiece>> usable, IReadOnlyList<OutfitCandidate?> outfits, TripOptions tripOptions)
    {
        var uses = outfits.OfType<OutfitCandidate>().SelectMany(TripPlanner.Core).GroupBy(piece => piece.Key).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var neighbours = new[] { day - 1, day + 1 }
            .Where(index => index >= 0 && index < outfits.Count && outfits[index] is not null)
            .SelectMany(index => TripPlanner.Core(outfits[index]!))
            .Select(piece => piece.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var slot in Checked)
        {
            var blocked = usable[slot].All(piece =>
                (TripRules.Cap(slot, tripOptions) is { } cap && uses.GetValueOrDefault(piece.Key) >= cap)
                || (TripRules.NotTwoDaysRunning(slot, tripOptions) && neighbours.Contains(piece.Key)));

            if (blocked)
            {
                return slot;
            }
        }

        return OutfitSlot.Top;
    }
}
