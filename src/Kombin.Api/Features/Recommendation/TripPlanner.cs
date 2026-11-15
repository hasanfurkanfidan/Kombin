using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

// TRIP PLANNER - the smallest set of the user's own pieces that dresses every day of a trip. Design: docs/design/budget-and-trips.md,
// section 2. No rules of its own about what looks good: every day's outfits come from OutfitGenerator and OutfitScoring, at or above the
// acceptance threshold. Change a constant here, in its comment and in the doc together.
//
//   WEAR RULES (TripRules), checked on every assignment:
//     - two consecutive days never wear the same outfit (same core pieces);
//     - a top at most TopWears (2) times per trip and never two days running; with laundry no cap, still not two days running;
//     - a bottom at most BottomWears (3) times per trip; with laundry no cap; two days running is fine;
//     - shoes never two days running when allowRepeatShoes is false; otherwise free;
//     - a layer is free (it is worn over everything).
//
//   POOLS: per day, the pieces the day's per-piece rules allow (OutfitGenerator.PieceScore), ranked by
//          piece score + VersatilityWeight × companions / the wardrobe's most + CoverageWeight × share of trip days the piece can serve,
//          the generator's 10/10/6/5 per slot kept. Pieces that work on many days and with many others fill every day's pool.
//   OPTIONS: OutfitGenerator.Generate on the day's pool (wardrobe only), best OptionsPerDay kept.
//   GREEDY: the day with the fewest options first; it takes the allowed option adding the fewest new pieces, then the higher score, then
//           the hash.
//   SWAP:   up to SwapRounds rounds: each packed piece, fewest uses first, is dropped if every day wearing it can be re-dressed from the
//           rest of the set; then every day takes the best-scoring allowed option inside the set. Days left without an outfit try once
//           more with new pieces.
//   REPAIR: when a day is still bare although every day has options (an early greedy choice used up what a later day needed), a
//           depth-first search in date order, packed pieces first, at most RepairSteps steps, looks for a plan that dresses every day;
//           if it finds one, the swap rounds run again on it.
//   MAX PIECES: while the set is larger, the day whose pieces are the most its own (sum of 1 / uses) loses its outfit, the later day on
//           ties.

/// <summary>What the user allows: at most <c>MaxPieces</c> packed (none: no limit), shoes worn on consecutive days, laundry on the way.</summary>
internal sealed record TripOptions(int? MaxPieces, bool AllowRepeatShoes, bool Laundry);

/// <summary>One day of the trip and the context its outfit is judged in (weather, occasion or persona band).</summary>
internal sealed record TripDay(DateOnly Date, OutfitContext Context);

/// <summary>Per day the chosen outfit (null: none), the packed pieces, and the days (indexes) that lost their outfit to <c>MaxPieces</c>.</summary>
internal sealed record TripPlanResult(IReadOnlyList<OutfitCandidate?> Outfits, IReadOnlyList<OutfitPiece> Packed, IReadOnlyList<int> DroppedDays);

internal static class TripRules
{
    public const int TopWears = 2;

    public const int BottomWears = 3;

    /// <summary>How many times a piece of <paramref name="slot"/> may be worn in one trip; null for no limit.</summary>
    public static int? Cap(OutfitSlot slot, TripOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return slot switch
        {
            OutfitSlot.Top when !options.Laundry => TopWears,
            OutfitSlot.Bottom when !options.Laundry => BottomWears,
            _ => null,
        };
    }

    /// <summary>Whether a piece of <paramref name="slot"/> may not be worn two days running.</summary>
    public static bool NotTwoDaysRunning(OutfitSlot slot, TripOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return slot == OutfitSlot.Top || (slot == OutfitSlot.Shoes && !options.AllowRepeatShoes);
    }

    /// <summary>Whether a whole plan (one outfit per day, null for none) keeps the wear rules. For tests and checks.</summary>
    public static bool Keeps(IReadOnlyList<OutfitCandidate?> outfits, TripOptions options)
    {
        ArgumentNullException.ThrowIfNull(outfits);

        var uses = new Dictionary<string, (OutfitSlot Slot, int Count)>(StringComparer.Ordinal);
        for (var day = 0; day < outfits.Count; day++)
        {
            if (outfits[day] is not { } outfit)
            {
                continue;
            }

            if (day > 0 && outfits[day - 1] is { } previous)
            {
                if (previous.Hash == outfit.Hash)
                {
                    return false;
                }

                foreach (var piece in TripPlanner.Core(outfit))
                {
                    if (NotTwoDaysRunning(piece.Slot, options) && TripPlanner.Core(previous).Any(other => other.Key == piece.Key))
                    {
                        return false;
                    }
                }
            }

            foreach (var piece in TripPlanner.Core(outfit))
            {
                var count = uses.TryGetValue(piece.Key, out var used) ? used.Count + 1 : 1;
                uses[piece.Key] = (piece.Slot, count);
                if (Cap(piece.Slot, options) is { } cap && count > cap)
                {
                    return false;
                }
            }
        }

        return true;
    }
}

internal static class TripPlanner
{
    public const int OptionsPerDay = 200;

    public const int SwapRounds = 4;

    public const int RepairSteps = 20_000;

    public const double VersatilityWeight = 0.3;

    public const double CoverageWeight = 0.2;

    private static readonly IReadOnlyDictionary<OutfitSlot, int> PoolSizes = new Dictionary<OutfitSlot, int>
    {
        [OutfitSlot.Top] = OutfitGenerator.TopsKept,
        [OutfitSlot.Bottom] = OutfitGenerator.BottomsKept,
        [OutfitSlot.Shoes] = OutfitGenerator.ShoesKept,
        [OutfitSlot.Outerwear] = OutfitGenerator.OuterwearKept,
    };

    /// <summary>The pieces a trip packs: everything but accessories (an outfit never needs one).</summary>
    public static IEnumerable<OutfitPiece> Core(OutfitCandidate outfit) => outfit.Pieces.Where(piece => piece.Slot != OutfitSlot.Accessory);

    public static TripPlanResult Plan(IReadOnlyList<TripDay> days, IReadOnlyList<OutfitPiece> wardrobe, IReadOnlyDictionary<Guid, int> companions, TripOptions options)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(wardrobe);
        ArgumentNullException.ThrowIfNull(companions);

        return Solve(OptionsFor(days, wardrobe, companions), options);
    }

    /// <summary>Every day's acceptable outfits from its pool (see POOLS and OPTIONS), best first.</summary>
    public static List<List<OutfitCandidate>> OptionsFor(IReadOnlyList<TripDay> days, IReadOnlyList<OutfitPiece> wardrobe, IReadOnlyDictionary<Guid, int> companions)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(wardrobe);
        ArgumentNullException.ThrowIfNull(companions);

        var mostCompanions = Math.Max(1, companions.Values.DefaultIfEmpty(0).Max());
        var coverage = wardrobe.ToDictionary(
            piece => piece.Key,
            piece => days.Count == 0 ? 0 : days.Count(day => OutfitGenerator.PieceScore(piece, day.Context) is not null) / (double)days.Count,
            StringComparer.Ordinal);

        var empty = new HashSet<string>(StringComparer.Ordinal);
        var options = new List<List<OutfitCandidate>>(days.Count);

        foreach (var day in days)
        {
            var pool = new List<OutfitPiece>();
            foreach (var (slot, size) in PoolSizes)
            {
                pool.AddRange(wardrobe
                    .Where(piece => piece.Slot == slot)
                    .Select(piece => (Piece: piece, Score: OutfitGenerator.PieceScore(piece, day.Context)))
                    .Where(scored => scored.Score is not null)
                    .Select(scored => (scored.Piece, Value: scored.Score!.Value
                        + (VersatilityWeight * companions.GetValueOrDefault(scored.Piece.Id) / mostCompanions)
                        + (CoverageWeight * coverage[scored.Piece.Key])))
                    .OrderByDescending(scored => scored.Value)
                    .ThenBy(scored => scored.Piece.Key, StringComparer.Ordinal)
                    .Take(size)
                    .Select(scored => scored.Piece));
            }

            var generated = OutfitGenerator.Generate(new GenerationRequest(pool, [], day.Context, null, 0, empty));
            options.Add([.. generated.Take(OptionsPerDay)]);
        }

        return options;
    }

    /// <summary>The search itself (GREEDY, SWAP, MAX PIECES) on given options; pure and deterministic.</summary>
    public static TripPlanResult Solve(IReadOnlyList<IReadOnlyList<OutfitCandidate>> options, TripOptions tripOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tripOptions);

        var state = new Assignment(options, tripOptions);

        foreach (var day in Enumerable.Range(0, options.Count).OrderBy(day => options[day].Count).ThenBy(day => day))
        {
            state.Set(day, state.Cheapest(day));
        }

        Swap(state);

        for (var day = 0; day < options.Count; day++)
        {
            if (state.Choice[day] < 0)
            {
                state.Set(day, state.Cheapest(day));
            }
        }

        if (state.Choice.Any(choice => choice < 0) && options.All(day => day.Count > 0) && state.CoverAll(RepairSteps))
        {
            Swap(state);
        }

        var dropped = new List<int>();
        if (tripOptions.MaxPieces is { } max)
        {
            while (state.PackedCount > max && state.DropMostOwnDay() is { } day)
            {
                dropped.Add(day);
            }
        }

        return new TripPlanResult(state.Outfits(), state.Packed(), dropped);
    }

    private static void Swap(Assignment state)
    {
        for (var round = 0; round < SwapRounds; round++)
        {
            var changed = state.EliminatePieces();
            changed |= state.ImproveScores();
            if (!changed)
            {
                break;
            }
        }
    }

    /// <summary>One option index per day (-1: none) with the piece use counts kept current.</summary>
    private sealed class Assignment(IReadOnlyList<IReadOnlyList<OutfitCandidate>> options, TripOptions tripOptions)
    {
        private readonly Dictionary<string, int> _uses = new(StringComparer.Ordinal);

        private readonly Dictionary<string, OutfitPiece> _pieces = new(StringComparer.Ordinal);

        public int[] Choice { get; } = [.. Enumerable.Repeat(-1, options.Count)];

        public int PackedCount => _uses.Count;

        public OutfitCandidate? At(int day) => day >= 0 && day < Choice.Length && Choice[day] >= 0 ? options[day][Choice[day]] : null;

        public void Set(int day, int choice)
        {
            if (At(day) is { } old)
            {
                foreach (var piece in Core(old))
                {
                    if (--_uses[piece.Key] == 0)
                    {
                        _uses.Remove(piece.Key);
                        _pieces.Remove(piece.Key);
                    }
                }
            }

            Choice[day] = choice;

            if (At(day) is { } current)
            {
                foreach (var piece in Core(current))
                {
                    _uses[piece.Key] = _uses.GetValueOrDefault(piece.Key) + 1;
                    _pieces[piece.Key] = piece;
                }
            }
        }

        /// <summary>Whether <paramref name="candidate"/> may be worn on <paramref name="day"/> given the other days (the day's own outfit not counted).</summary>
        public bool Allowed(int day, OutfitCandidate candidate)
        {
            var own = At(day);

            foreach (var neighbour in new[] { At(day - 1), At(day + 1) })
            {
                if (neighbour is null)
                {
                    continue;
                }

                if (neighbour.Hash == candidate.Hash)
                {
                    return false;
                }

                foreach (var piece in Core(candidate))
                {
                    if (TripRules.NotTwoDaysRunning(piece.Slot, tripOptions) && Core(neighbour).Any(other => other.Key == piece.Key))
                    {
                        return false;
                    }
                }
            }

            foreach (var piece in Core(candidate))
            {
                if (TripRules.Cap(piece.Slot, tripOptions) is not { } cap)
                {
                    continue;
                }

                var used = _uses.GetValueOrDefault(piece.Key) - (own is not null && Core(own).Any(other => other.Key == piece.Key) ? 1 : 0);
                if (used + 1 > cap)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The allowed option adding the fewest pieces to the set, then the best score (options are sorted best first), else -1.</summary>
        public int Cheapest(int day)
        {
            var best = -1;
            var bestNew = int.MaxValue;

            for (var index = 0; index < options[day].Count; index++)
            {
                var candidate = options[day][index];
                var added = Core(candidate).Count(piece => !_uses.ContainsKey(piece.Key));
                if (added < bestNew && Allowed(day, candidate))
                {
                    (best, bestNew) = (index, added);
                    if (added == 0)
                    {
                        break;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// REPAIR: a depth-first search in date order for a plan that dresses every day, trying at each day the allowed options that add the fewest
        /// pieces to what is packed so far (then the better score), at most <paramref name="maxSteps"/> options tried. On failure the plan is restored.
        /// </summary>
        public bool CoverAll(int maxSteps)
        {
            var saved = Choice.ToArray();
            var steps = 0;

            for (var day = 0; day < Choice.Length; day++)
            {
                Set(day, -1);
            }

            bool Dress(int day)
            {
                if (day == Choice.Length)
                {
                    return true;
                }

                var order = Enumerable.Range(0, options[day].Count)
                    .OrderBy(index => Core(options[day][index]).Count(piece => !_uses.ContainsKey(piece.Key)))
                    .ThenBy(index => index)
                    .ToList();

                foreach (var index in order)
                {
                    if (++steps > maxSteps)
                    {
                        return false;
                    }

                    if (!Allowed(day, options[day][index]))
                    {
                        continue;
                    }

                    Set(day, index);
                    if (Dress(day + 1))
                    {
                        return true;
                    }

                    Set(day, -1);
                }

                return false;
            }

            if (Dress(0))
            {
                return true;
            }

            for (var day = 0; day < Choice.Length; day++)
            {
                Set(day, -1);
            }

            for (var day = 0; day < Choice.Length; day++)
            {
                Set(day, saved[day]);
            }

            return false;
        }

        /// <summary>Drops every packed piece whose days can all be re-dressed from the rest of the set (fewest uses first). True if any was dropped.</summary>
        public bool EliminatePieces()
        {
            var changed = false;
            var order = _uses.OrderBy(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key).ToList();

            foreach (var key in order)
            {
                if (!_uses.ContainsKey(key))
                {
                    continue;
                }

                var days = Enumerable.Range(0, Choice.Length).Where(day => At(day) is { } outfit && Core(outfit).Any(piece => piece.Key == key)).ToList();
                var saved = days.ToDictionary(day => day, day => Choice[day]);
                var allowedKeys = _uses.Keys.Where(other => other != key).ToHashSet(StringComparer.Ordinal);
                var success = true;

                foreach (var day in days)
                {
                    var replacement = -1;
                    for (var index = 0; index < options[day].Count; index++)
                    {
                        var candidate = options[day][index];
                        if (Core(candidate).All(piece => allowedKeys.Contains(piece.Key)) && Allowed(day, candidate))
                        {
                            replacement = index;
                            break;
                        }
                    }

                    if (replacement < 0)
                    {
                        success = false;
                        break;
                    }

                    Set(day, replacement);
                }

                if (success && !_uses.ContainsKey(key))
                {
                    changed = true;
                    continue;
                }

                foreach (var (day, choice) in saved)
                {
                    Set(day, choice);
                }
            }

            return changed;
        }

        /// <summary>Each day takes the best-scoring allowed option inside the packed set. True if any day changed.</summary>
        public bool ImproveScores()
        {
            var changed = false;

            for (var day = 0; day < Choice.Length; day++)
            {
                if (Choice[day] < 0)
                {
                    continue;
                }

                for (var index = 0; index < Choice[day]; index++)
                {
                    var candidate = options[day][index];
                    if (Core(candidate).All(piece => _uses.ContainsKey(piece.Key)) && Allowed(day, candidate))
                    {
                        Set(day, index);
                        changed = true;
                        break;
                    }
                }
            }

            return changed;
        }

        /// <summary>Takes the outfit off the day whose pieces are the most its own and returns it; null when no day has an outfit.</summary>
        public int? DropMostOwnDay()
        {
            var victim = Enumerable.Range(0, Choice.Length)
                .Where(day => At(day) is not null)
                .Select(day => (Day: day, Own: Core(At(day)!).Sum(piece => 1.0 / _uses[piece.Key])))
                .OrderByDescending(entry => entry.Own)
                .ThenByDescending(entry => entry.Day)
                .Select(entry => (int?)entry.Day)
                .FirstOrDefault();

            if (victim is { } day)
            {
                Set(day, -1);
            }

            return victim;
        }

        public List<OutfitCandidate?> Outfits() => [.. Enumerable.Range(0, Choice.Length).Select(At)];

        public List<OutfitPiece> Packed() =>
            [.. _pieces.Values.OrderBy(piece => piece.Slot).ThenByDescending(piece => _uses[piece.Key]).ThenBy(piece => piece.Key, StringComparer.Ordinal)];
    }
}
