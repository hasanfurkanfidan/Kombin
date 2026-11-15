using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using static Kombin.Api.Tests.OutfitPieces;

namespace Kombin.Api.Tests;

public class TripPlannerTests(ITestOutputHelper output)
{
    private static readonly TripOptions Default = new(null, AllowRepeatShoes: true, Laundry: false);

    private static readonly IReadOnlyDictionary<Guid, int> NoCompanions = new Dictionary<Guid, int>();

    /// <summary>Mild autumn days without a forecast (a layer is optional), no occasion.</summary>
    private static List<TripDay> Days(int count, WeatherConditions? weather = null, Occasion? occasion = null) =>
        [.. Enumerable.Range(0, count).Select(index => new TripDay(Day.AddDays(index), OutfitContext.Create(occasion, PersonaTaste.None, weather ?? WeatherRules.FromSeason(Day), Day.AddDays(index))))];

    private static readonly IReadOnlyList<OutfitPiece> Capsule =
    [
        Tee(ProductColor.White, 2, "tee-white"),
        Tee(ProductColor.Black, 2, "tee-black"),
        Tee(ProductColor.Grey, 2, "tee-grey"),
        Tee(ProductColor.Navy, 2, "tee-navy"),
        Jeans(ProductColor.Blue, 2, "jeans-blue"),
        Jeans(ProductColor.Black, 2, "jeans-black"),
        Sneaker(ProductColor.White, 2, "sneaker-white"),
        Sneaker(ProductColor.Black, 2, "sneaker-black"),
        Jacket(ProductColor.Navy, 3, "jacket-navy"),
    ];

    private static TripPlanResult Plan(IReadOnlyList<TripDay> days, IReadOnlyList<OutfitPiece>? wardrobe = null, TripOptions? options = null) =>
        TripPlanner.Plan(days, wardrobe ?? Capsule, NoCompanions, options ?? Default);

    [Fact]
    public void ThreeDaysNeedTwoTopsOneBottomAndOnePairOfShoes()
    {
        var plan = Plan(Days(3));

        Assert.All(plan.Outfits, outfit => Assert.NotNull(outfit));
        Assert.Equal(4, plan.Packed.Count);
        Assert.Equal(2, plan.Packed.Count(piece => piece.Slot == OutfitSlot.Top));
        Assert.True(TripRules.Keeps(plan.Outfits, Default));
    }

    [Fact]
    public void WithoutLaundrySixDaysNeedThreeTopsAndTwoBottomsWithLaundryFewer()
    {
        var withoutLaundry = Plan(Days(6));
        var withLaundry = Plan(Days(6), options: Default with { Laundry = true });

        Assert.All(withoutLaundry.Outfits, outfit => Assert.NotNull(outfit));
        Assert.Equal(3, withoutLaundry.Packed.Count(piece => piece.Slot == OutfitSlot.Top));
        Assert.Equal(2, withoutLaundry.Packed.Count(piece => piece.Slot == OutfitSlot.Bottom));
        Assert.True(withLaundry.Packed.Count < withoutLaundry.Packed.Count);
        Assert.Equal(2, withLaundry.Packed.Count(piece => piece.Slot == OutfitSlot.Top));
        Assert.True(TripRules.Keeps(withLaundry.Outfits, Default with { Laundry = true }));
    }

    [Fact]
    public void ShoesThatMayNotRepeatOnConsecutiveDaysMeanTwoPairs()
    {
        var options = Default with { AllowRepeatShoes = false };
        var plan = Plan(Days(3), options: options);

        Assert.Equal(2, plan.Packed.Count(piece => piece.Slot == OutfitSlot.Shoes));
        Assert.True(TripRules.Keeps(plan.Outfits, options));
    }

    [Fact]
    public void TwoConsecutiveDaysNeverWearTheSameOutfit()
    {
        var plan = Plan(Days(5), options: Default with { Laundry = true });

        for (var day = 1; day < plan.Outfits.Count; day++)
        {
            Assert.NotEqual(plan.Outfits[day - 1]!.Hash, plan.Outfits[day]!.Hash);
        }
    }

    [Fact]
    public void TheSameInputsGiveTheSamePlan()
    {
        var first = Plan(Days(7));
        var again = Plan(Days(7));

        Assert.Equal(first.Outfits.Select(outfit => outfit?.Hash), again.Outfits.Select(outfit => outfit?.Hash));
        Assert.Equal(first.Packed.Select(piece => piece.Key), again.Packed.Select(piece => piece.Key));
    }

    [Fact]
    public void APieceLimitDropsDaysRatherThanBreakingIt()
    {
        var plan = Plan(Days(6), options: Default with { MaxPieces = 4 });

        Assert.True(plan.Packed.Count <= 4);
        Assert.NotEmpty(plan.DroppedDays);
        Assert.All(plan.DroppedDays, day => Assert.Null(plan.Outfits[day]));
    }

    [Fact]
    public void AWardrobeWithoutShoesDressesNoDayAndSaysShoesAreMissing()
    {
        var wardrobe = Capsule.Where(piece => piece.Slot != OutfitSlot.Shoes).ToList();
        var days = Days(3);
        var options = TripPlanner.OptionsFor(days, wardrobe, NoCompanions);
        var plan = TripPlanner.Solve(options, Default);

        Assert.All(plan.Outfits, outfit => Assert.Null(outfit));
        Assert.Empty(plan.Packed);

        var gap = Assert.Single(TripGaps.Analyze(days, wardrobe, options, plan.Outfits, plan.DroppedDays, Default));
        Assert.Equal(OutfitSlot.Shoes, gap.Slot);
        Assert.Equal(TripGapReason.Missing, gap.Reason);
        Assert.Equal([0, 1, 2], gap.Days);
    }

    [Fact]
    public void ColdDaysWithOnlyABlazerAsALayerAskForACoatBecauseOfTheWeather()
    {
        var cold = WeatherRules.From(new WeatherSnapshot("Cold", Day, -3, 3, 0, 0, 0));
        var wardrobe = Capsule.Where(piece => piece.Slot != OutfitSlot.Outerwear).Append(Blazer(ProductColor.Navy, 3, "blazer")).ToList();
        var days = Days(2, cold);
        var options = TripPlanner.OptionsFor(days, wardrobe, NoCompanions);
        var plan = TripPlanner.Solve(options, Default);

        var gap = Assert.Single(TripGaps.Analyze(days, wardrobe, options, plan.Outfits, plan.DroppedDays, Default));
        Assert.Equal(OutfitSlot.Outerwear, gap.Slot);
        Assert.Equal(TripGapReason.Weather, gap.Reason);
    }

    [Fact]
    public void RunningOutOfTopsIsARepetitionGapOnTheTop()
    {
        // One top only: day 1 wears it, day 2 may not (never two days running), day 3 may again (second wear).
        var wardrobe = new[] { Tee(ProductColor.White, 2, "only-top"), Jeans(ProductColor.Blue, 2, "j"), Sneaker(ProductColor.White, 2, "s") };
        var days = Days(3);
        var options = TripPlanner.OptionsFor(days, wardrobe, NoCompanions);
        var plan = TripPlanner.Solve(options, Default);

        Assert.Equal(2, plan.Outfits.Count(outfit => outfit is not null));
        var gap = Assert.Single(TripGaps.Analyze(days, wardrobe, options, plan.Outfits, plan.DroppedDays, Default));
        Assert.Equal((OutfitSlot.Top, TripGapReason.Repetition), (gap.Slot, gap.Reason));
    }

    [Fact]
    public void VersatilePiecesArePreferredWhenAPoolMustChoose()
    {
        // Twelve tops for ten pool places: the two with companions win their place over equal tops without.
        var tops = Enumerable.Range(0, 12).Select(index => Tee(ProductColor.White, 2, $"top-{index}")).ToList();
        var wardrobe = tops.Concat([Jeans(ProductColor.Blue, 2, "j"), Sneaker(ProductColor.White, 2, "s")]).ToList();
        var companions = new Dictionary<Guid, int> { [tops[10].Id] = 9, [tops[11].Id] = 9 };

        var options = TripPlanner.OptionsFor(Days(1), wardrobe, companions);

        var used = options[0].SelectMany(outfit => outfit.Pieces).Select(piece => piece.Id).ToHashSet();
        Assert.Contains(tops[10].Id, used);
        Assert.Contains(tops[11].Id, used);
    }

    /// <summary>
    /// The number behind the design doc's "distance from optimal": generated wardrobes (7-13 pieces, 3-5 days, seeded) planned by the greedy
    /// search and by an exact search over subsets of the pieces. The greedy plan is never more than one piece above the optimum and is optimal in
    /// most cases; the exact numbers are printed.
    /// </summary>
    [Fact]
    public void TheGreedyPlanIsAtMostOnePieceAboveTheOptimum()
    {
        var random = new Random(20261115);
        var compared = 0;
        var equal = 0;
        var excess = 0;
        var worst = 0;
        var undecided = 0;

        for (var instance = 0; instance < 300; instance++)
        {
            var wardrobe = GeneratedWardrobe(random, instance);
            var days = GeneratedDays(random);
            var options = new TripOptions(null, random.Next(3) > 0, random.Next(4) == 0);
            var dayOptions = TripPlanner.OptionsFor(days, wardrobe, NoCompanions);
            var greedy = TripPlanner.Solve(dayOptions, options);

            Assert.True(TripRules.Keeps(greedy.Outfits, options));

            var (decided, optimum) = Optimum(dayOptions, options, greedy.Outfits.All(outfit => outfit is not null));
            if (!decided)
            {
                undecided++;
                continue;
            }

            if (optimum is null)
            {
                Assert.Contains(greedy.Outfits, outfit => outfit is null);
                continue;
            }

            Assert.True(greedy.Outfits.All(outfit => outfit is not null), $"instance {instance}: the greedy plan left a day bare that the optimum dresses");

            var gap = greedy.Packed.Count - optimum.Value;
            Assert.True(gap >= 0);
            compared++;
            equal += gap == 0 ? 1 : 0;
            excess += gap;
            worst = Math.Max(worst, gap);
        }

        output.WriteLine($"undecided {undecided}, compared {compared}, optimal {equal} ({100.0 * equal / compared:F1} %), mean excess {(double)excess / compared:F3}, worst +{worst}");
        Assert.True(compared >= 220, $"only {compared} instances were coverable");
        Assert.True(worst <= 1, $"worst +{worst}");
        Assert.True(equal >= compared * 0.9, $"optimal in {equal} of {compared}");
    }

    private static List<OutfitPiece> GeneratedWardrobe(Random random, int instance)
    {
        ProductColor[] neutrals = [ProductColor.White, ProductColor.Black, ProductColor.Grey, ProductColor.Navy, ProductColor.Beige];
        ProductColor[] colours = [ProductColor.Red, ProductColor.Green, ProductColor.Blue];
        ProductColor Colour() => random.Next(4) == 0 ? colours[random.Next(colours.Length)] : neutrals[random.Next(neutrals.Length)];

        var pieces = new List<OutfitPiece>();
        void Add(string category, int count)
        {
            for (var index = 0; index < count; index++)
            {
                pieces.Add(Piece(category, Colour(), random.Next(2, 4), name: $"{instance}-{category}-{index}"));
            }
        }

        Add("ust-giyim/tisort", random.Next(3, 6));
        Add("alt-giyim/pantolon", random.Next(2, 4));
        Add("ayakkabi/sneaker", random.Next(1, 3));
        Add("dis-giyim/ceket", random.Next(1, 3));

        return pieces;
    }

    private static List<TripDay> GeneratedDays(Random random)
    {
        var count = random.Next(3, 6);
        var rainy = WeatherRules.From(new WeatherSnapshot("Rain", Day, 12, 17, 80, 4, 61));
        var mild = WeatherRules.FromSeason(Day);

        return [.. Enumerable.Range(0, count).Select(index => new TripDay(Day.AddDays(index), OutfitContext.Create(null, PersonaTaste.None, random.Next(4) == 0 ? rainy : mild, Day.AddDays(index))))];
    }

    /// <summary>
    /// The smallest number of pieces that dresses every day within the wear rules, by trying subsets of the pieces (as bit masks) in increasing size;
    /// null if none does. Each feasibility search is bounded: an instance whose search runs out of steps is reported undecided and left out.
    /// </summary>
    private static (bool Decided, int? Optimum) Optimum(List<List<OutfitCandidate>> options, TripOptions tripOptions, bool knownFeasible)
    {
        var pieces = options.SelectMany(day => day).SelectMany(TripPlanner.Core).Select(piece => piece.Key).Distinct().Order(StringComparer.Ordinal).ToList();
        if (options.Any(day => day.Count == 0))
        {
            return (true, null);
        }

        if (!knownFeasible)
        {
            var whole = Dress(options, tripOptions);
            if (whole is not true)
            {
                return (whole is false, null);
            }
        }

        var bit = pieces.Select((key, index) => (key, index)).ToDictionary(entry => entry.key, entry => 1UL << entry.index, StringComparer.Ordinal);
        var masks = options.Select(day => day.Select(outfit => TripPlanner.Core(outfit).Aggregate(0UL, (mask, piece) => mask | bit[piece.Key])).ToList()).ToList();

        // Every outfit has a top, a bottom and shoes, so no smaller set can work.
        for (var size = 3; size <= pieces.Count; size++)
        {
            // Gosper's hack: every mask of `size` bits below 2^pieces, in increasing order.
            for (var subset = (1UL << size) - 1; subset < (1UL << pieces.Count); subset = Next(subset))
            {
                var restricted = new List<List<OutfitCandidate>>(options.Count);
                for (var day = 0; day < options.Count; day++)
                {
                    var allowed = new List<OutfitCandidate>();
                    for (var index = 0; index < options[day].Count; index++)
                    {
                        if ((masks[day][index] & ~subset) == 0)
                        {
                            allowed.Add(options[day][index]);
                        }
                    }

                    if (allowed.Count == 0)
                    {
                        break;
                    }

                    restricted.Add(allowed);
                }

                if (restricted.Count < options.Count)
                {
                    continue;
                }

                switch (Dress(restricted, tripOptions))
                {
                    case true:
                        return (true, size);
                    case null:
                        return (false, null);
                }
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Whether every day can be dressed from <paramref name="options"/> within the wear rules (checked incrementally against the previous day and the
    /// use counts; <see cref="TripRules.Keeps"/> confirms the result); null when 20,000 steps did not tell.
    /// </summary>
    private static bool? Dress(List<List<OutfitCandidate>> options, TripOptions tripOptions)
    {
        var steps = 0;
        var chosen = new OutfitCandidate?[options.Count];
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);

        bool Fits(int day, OutfitCandidate outfit)
        {
            if (day > 0 && chosen[day - 1] is { } previous)
            {
                if (previous.Hash == outfit.Hash)
                {
                    return false;
                }

                foreach (var piece in TripPlanner.Core(outfit))
                {
                    if (TripRules.NotTwoDaysRunning(piece.Slot, tripOptions) && TripPlanner.Core(previous).Any(other => other.Key == piece.Key))
                    {
                        return false;
                    }
                }
            }

            return TripPlanner.Core(outfit).All(piece => TripRules.Cap(piece.Slot, tripOptions) is not { } cap || uses.GetValueOrDefault(piece.Key) < cap);
        }

        bool? Search(int day)
        {
            if (day == options.Count)
            {
                return true;
            }

            foreach (var outfit in options[day])
            {
                if (++steps > 20_000)
                {
                    return null;
                }

                if (!Fits(day, outfit))
                {
                    continue;
                }

                chosen[day] = outfit;
                foreach (var piece in TripPlanner.Core(outfit))
                {
                    uses[piece.Key] = uses.GetValueOrDefault(piece.Key) + 1;
                }

                var result = Search(day + 1);
                if (result is not false)
                {
                    return result;
                }

                foreach (var piece in TripPlanner.Core(outfit))
                {
                    uses[piece.Key]--;
                }

                chosen[day] = null;
            }

            return false;
        }

        var dressed = Search(0);
        Assert.True(dressed is not true || TripRules.Keeps(chosen, tripOptions));

        return dressed;
    }

    private static ulong Next(ulong mask)
    {
        var lowest = mask & (ulong)-(long)mask;
        var ripple = mask + lowest;

        return ripple | (((mask ^ ripple) >> 2) / lowest);
    }
}
