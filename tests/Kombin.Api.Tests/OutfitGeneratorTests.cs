using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using static Kombin.Api.Tests.OutfitPieces;

namespace Kombin.Api.Tests;

public class OutfitGeneratorTests
{
    /// <summary>A small but varied smart-casual wardrobe: enough for several outfits that differ in two pieces.</summary>
    private static readonly IReadOnlyList<OutfitPiece> Wardrobe =
    [
        Tee(ProductColor.White, 2, "tee-white"),
        Tee(ProductColor.Black, 2, "tee-black"),
        Shirt(ProductColor.Blue, 3, "shirt-blue"),
        Shirt(ProductColor.White, 3, "shirt-white"),
        Piece("ust-giyim/triko", ProductColor.Beige, 3, name: "knit-beige"),
        Tee(ProductColor.Green, 2, "tee-green"),
        Jeans(ProductColor.Blue, 2, "jeans-blue"),
        Jeans(ProductColor.Black, 2, "jeans-black"),
        Trousers(ProductColor.Navy, 3, "trousers-navy"),
        Trousers(ProductColor.Beige, 3, "trousers-beige"),
        Sneaker(ProductColor.White, 2, "sneaker-white"),
        Sneaker(ProductColor.Black, 2, "sneaker-black"),
        Loafer(ProductColor.Brown, 3, "loafer-brown"),
        Boot(ProductColor.Black, 3, "boot-black"),
        Jacket(ProductColor.Navy, 3, "jacket-navy"),
        Piece("dis-giyim/mont", ProductColor.Black, 2, name: "puffer-black"),
        Piece("aksesuar/kemer", ProductColor.Brown, 3, name: "belt-brown"),
    ];

    private static readonly IReadOnlySet<string> NoExclusions = new HashSet<string>();

    private static List<OutfitCandidate> Generate(
        IReadOnlyList<OutfitPiece>? wardrobe = null, OutfitContext? context = null, IReadOnlyList<OutfitPiece>? catalog = null, int maxGaps = 0, OutfitPiece? anchor = null, IReadOnlySet<string>? excluded = null) =>
        OutfitGenerator.Generate(new GenerationRequest(wardrobe ?? Wardrobe, catalog ?? [], context ?? Context(), anchor, maxGaps, excluded ?? NoExclusions));

    [Fact]
    public void TheSameInputsGiveTheSameOutfitsInTheSameOrder()
    {
        var first = Generate();
        var second = Generate();

        Assert.NotEmpty(first);
        Assert.Equal(first.Select(candidate => candidate.Hash), second.Select(candidate => candidate.Hash));
        Assert.Equal(
            OutfitGenerator.PickDaily(first, 3, Day, 2).Select(candidate => candidate.Hash),
            OutfitGenerator.PickDaily(second, 3, Day, 2).Select(candidate => candidate.Hash));
    }

    [Fact]
    public void EveryGeneratedOutfitIsWearableAndRankedBestFirst()
    {
        var ranked = Generate();

        Assert.All(ranked, candidate =>
        {
            Assert.True(candidate.Score >= OutfitScoring.AcceptanceThreshold);
            Assert.Single(candidate.Pieces, piece => piece.Slot == OutfitSlot.Top);
            Assert.Single(candidate.Pieces, piece => piece.Slot == OutfitSlot.Bottom);
            Assert.Single(candidate.Pieces, piece => piece.Slot == OutfitSlot.Shoes);
            Assert.True(ColorRules.Judge(candidate.Pieces).Allowed);
            Assert.True(PatternRules.Judge(candidate.Pieces).Allowed);
        });
        Assert.Equal(ranked.OrderByDescending(candidate => candidate.Score).Select(candidate => candidate.Score), ranked.Select(candidate => candidate.Score));
    }

    [Fact]
    public void ConsecutiveSeedsGiveOutfitsThatDifferInAtLeastTwoPieces()
    {
        var ranked = Generate();

        for (var seed = 0; seed < 20; seed++)
        {
            var current = OutfitGenerator.PickDaily(ranked, seed, Day, 0)[0];
            var next = OutfitGenerator.PickDaily(ranked, seed + 1, Day, 0)[0];

            Assert.True(OutfitGenerator.Difference(current, next) >= OutfitGenerator.MinDifference, $"seeds {seed} and {seed + 1}");
        }
    }

    [Fact]
    public void TheAlternativesDifferFromTheOutfitAndFromEachOther()
    {
        var picks = OutfitGenerator.PickDaily(Generate(), 0, Day, 2);

        Assert.Equal(3, picks.Count);
        Assert.Equal(3, picks.Select(candidate => candidate.Hash).Distinct().Count());
        Assert.True(OutfitGenerator.Difference(picks[0], picks[1]) >= 2);
        Assert.True(OutfitGenerator.Difference(picks[1], picks[2]) >= 2);
        Assert.True(OutfitGenerator.Difference(picks[0], picks[2]) >= 2);
    }

    [Fact]
    public void TomorrowStartsAtADifferentOutfitThanToday()
    {
        var ranked = Generate();

        Assert.NotEqual(OutfitGenerator.PickDaily(ranked, 0, Day, 0)[0].Hash, OutfitGenerator.PickDaily(ranked, 0, Day.AddDays(1), 0)[0].Hash);
    }

    [Fact]
    public void AWardrobeWithOneOutfitAlwaysGivesThatOne()
    {
        var ranked = Generate([Tee(ProductColor.White), Jeans(), Sneaker(ProductColor.White)]);

        Assert.Single(ranked);
        Assert.Equal(ranked[0].Hash, OutfitGenerator.PickDaily(ranked, 7, Day, 2).Single().Hash);
    }

    [Fact]
    public void AvoidedColoursAndDismissedOutfitsAreNeverSuggested()
    {
        var taste = PersonaTaste.None with { AvoidedColors = new HashSet<ProductColor> { ProductColor.Green } };
        Assert.DoesNotContain(Generate(context: Context(taste: taste)), candidate => candidate.Pieces.Any(piece => piece.Color == ProductColor.Green));

        var all = Generate();
        var dismissed = new HashSet<string> { all[0].Hash };
        Assert.DoesNotContain(Generate(excluded: dismissed), candidate => candidate.Hash == all[0].Hash);
    }

    [Fact]
    public void APieceWornYesterdayRanksLower()
    {
        var fresh = Generate();
        var best = fresh[0];
        var worn = Wardrobe.Select(piece => best.Pieces.Any(used => used.Key == piece.Key && piece.Slot == OutfitSlot.Top) ? piece with { LastWornAt = Day.AddDays(-1) } : piece).ToList();

        var again = Generate(worn).Single(candidate => candidate.Hash == best.Hash);

        Assert.Equal(best.Score - OutfitScoring.RecentlyWornPenalty, again.Score);
    }

    [Fact]
    public void ACatalogPieceIsUsedOnlyWhenGapsAreAllowedAndOnlyOne()
    {
        // No shoes at home: nothing complete is possible.
        var wardrobe = Wardrobe.Where(piece => piece.Slot != OutfitSlot.Shoes).ToList();
        var shop = new[]
        {
            Piece("ayakkabi/sneaker", ProductColor.White, 2, name: "shop-sneaker", source: PieceSource.Catalog, price: 1200m),
            Piece("alt-giyim/pantolon", ProductColor.Grey, 3, name: "shop-trousers", source: PieceSource.Catalog, price: 900m),
        };

        Assert.Empty(Generate(wardrobe));
        Assert.Empty(Generate(wardrobe, catalog: shop, maxGaps: 0));

        var withGaps = Generate(wardrobe, catalog: shop, maxGaps: 1);
        Assert.NotEmpty(withGaps);
        Assert.All(withGaps, candidate => Assert.Equal(1, candidate.GapCount(null)));
        Assert.All(withGaps, candidate => Assert.Equal(OutfitCompleteness.NeedsOne, candidate.Completeness(null)));
    }

    [Fact]
    public void AHowToWearSetUsesNoCompanionMoreThanTwiceAndKeepsTheAnchor()
    {
        var anchor = Blazer(ProductColor.Navy, 4, "anchor-blazer") with { Source = PieceSource.Catalog };
        var ranked = Generate(anchor: anchor, context: Context(occasion: Occasion.Office));
        var picks = OutfitGenerator.PickAround(ranked, anchor.Key, 0, 5);

        Assert.InRange(picks.Count, 3, 5);
        Assert.All(picks, candidate => Assert.Contains(candidate.Pieces, piece => piece.Key == anchor.Key));

        var uses = picks.SelectMany(candidate => candidate.Pieces.Where(piece => piece.Key != anchor.Key)).GroupBy(piece => piece.Key);
        Assert.All(uses, group => Assert.True(group.Count() <= OutfitGenerator.MaxPieceUses, group.Key));

        // The anchor itself is not counted as a gap.
        Assert.All(picks, candidate => Assert.Equal(OutfitCompleteness.Complete, candidate.Completeness(anchor.Key)));
    }

    [Fact]
    public void AnAccessoryAnchorIsInEveryOutfitAndPartOfItsIdentity()
    {
        var bag = Piece("aksesuar/canta", ProductColor.Black, 3, name: "anchor-bag", source: PieceSource.Catalog);
        var otherBag = Piece("aksesuar/canta", ProductColor.Beige, 3, name: "other-bag", source: PieceSource.Catalog);

        var withBag = Generate(anchor: bag);
        var withOther = Generate(anchor: otherBag);

        Assert.NotEmpty(withBag);
        Assert.All(withBag, candidate => Assert.Contains(candidate.Pieces, piece => piece.Key == bag.Key));
        Assert.Empty(withBag.Select(candidate => candidate.Hash).Intersect(withOther.Select(candidate => candidate.Hash)));
    }

    [Fact]
    public void AHowToWearSeedReordersDeterministically()
    {
        var anchor = Shirt(ProductColor.White, 3, "anchor-shirt");
        var ranked = Generate(Wardrobe.Where(piece => piece.Key != anchor.Key).ToList(), anchor: anchor);

        var plain = OutfitGenerator.PickAround(ranked, anchor.Key, 0, 5).Select(candidate => candidate.Hash).ToList();
        var seeded = OutfitGenerator.PickAround(ranked, anchor.Key, 4, 5).Select(candidate => candidate.Hash).ToList();

        Assert.Equal(plain, OutfitGenerator.PickAround(ranked, anchor.Key, 0, 5).Select(candidate => candidate.Hash));
        Assert.Equal(seeded, OutfitGenerator.PickAround(ranked, anchor.Key, 4, 5).Select(candidate => candidate.Hash));
        Assert.Equal(ranked.Take(1).Select(candidate => candidate.Hash), plain.Take(1));
        Assert.Equal(0, OutfitGenerator.Jitter(0, "anything"));
        Assert.InRange(OutfitGenerator.Jitter(4, "anything"), 0, OutfitGenerator.JitterPoints);
    }

    [Fact]
    public void TheAnchorIsExemptFromThePersonasAvoidedColours()
    {
        var anchor = Tee(ProductColor.Green, 2, "anchor-green");
        var taste = PersonaTaste.None with { AvoidedColors = new HashSet<ProductColor> { ProductColor.Green } };

        var pieces = new[] { anchor, Jeans(), Sneaker(ProductColor.White) };

        Assert.False(OutfitScoring.Evaluate(pieces, Context(taste: taste)).Allowed);
        Assert.True(OutfitScoring.Evaluate(pieces, Context(taste: taste), anchor.Key).Allowed);
    }

    [Fact]
    public void AColdDayRequiresALayerAndTheScoreExplainsIt()
    {
        var cold = WeatherRules.From(new Features.Recommendation.Weather.WeatherSnapshot("İstanbul", Day, 1, 6, 0, 0, 1));
        var ranked = Generate(context: Context(weather: cold));

        Assert.NotEmpty(ranked);
        Assert.All(ranked, candidate => Assert.Contains(candidate.Pieces, piece => piece.Slot == OutfitSlot.Outerwear && piece.Leaf is "mont" or "kaban"));
        Assert.All(ranked, candidate => Assert.Equal(OutfitRule.Cold, candidate.Evaluation.Rule));
    }

    [Fact]
    public void AnAccessoryIsAddedOnlyWhenItKeepsTheOutfitAsGood()
    {
        var context = Context();
        var best = Generate()[0];
        var dressed = OutfitGenerator.WithAccessory(best, Wardrobe, context, null);

        Assert.Equal(best.Hash, dressed.Hash);
        Assert.True(dressed.Score >= best.Score);
        Assert.Equal(best.Evaluation.Rule, dressed.Evaluation.Rule);
    }

    [Fact]
    public void ThePersonasMatchScoresShapeTheRanking()
    {
        // A persona that loves the green tee (its MatchScorer score) and is lukewarm about everything else, then the other way round.
        var loved = Wardrobe.Select(piece => piece with { PersonaScore = piece.Key == Tee(ProductColor.Green, 2, "tee-green").Key ? 100 : 20 }).ToList();
        var disliked = Wardrobe.Select(piece => piece with { PersonaScore = piece.Key == Tee(ProductColor.Green, 2, "tee-green").Key ? 0 : 100 }).ToList();

        Assert.Contains(Generate(loved)[0].Pieces, piece => piece.Color == ProductColor.Green);
        Assert.DoesNotContain(Generate(disliked)[0].Pieces, piece => piece.Color == ProductColor.Green);
    }

    [Fact]
    public void ThePersonasFamilyWeightsCountWithoutAnOccasion()
    {
        var sporty = PersonaTaste.None with { Families = new Dictionary<StyleFamily, double> { [StyleFamily.Sporty] = 1.0 } };
        var pieces = new[] { Tee(ProductColor.Black, 1), Jeans(ProductColor.Black, 1), Sneaker(ProductColor.White, 1) };

        var withTaste = OutfitScoring.Evaluate(pieces, Context(taste: sporty));
        var without = OutfitScoring.Evaluate(pieces, Context());

        Assert.Equal(1.0 * 0.5, withTaste.Occasion - (without.Occasion - (OutfitScoring.UnknownFamilyWeight * 0.5)), 3);
    }
}

public class OutfitCounterTests
{
    [Fact]
    public void CountsCompleteCoreOutfitsThatPassTheRules()
    {
        IReadOnlyList<OutfitPiece> wardrobe =
        [
            Tee(ProductColor.White, 2, "a"),
            Tee(ProductColor.Red, 2, "b"),
            Jeans(ProductColor.Blue, 2, "c"),
            Trousers(ProductColor.Green, 2, "d"),
            Sneaker(ProductColor.White, 2, "e"),
        ];

        // white+jeans, white+green trousers, red+jeans; red+green is complementary and allowed too.
        Assert.Equal(4, OutfitCounter.Count(wardrobe));

        // Without shoes nothing is complete.
        Assert.Equal(0, OutfitCounter.Count([.. wardrobe.Where(piece => piece.Slot != OutfitSlot.Shoes)]));
    }

    [Fact]
    public void ClashingCombinationsAreNotCounted()
    {
        IReadOnlyList<OutfitPiece> wardrobe =
        [
            Tee(ProductColor.Red, 2, "a"),
            Trousers(ProductColor.Purple, 2, "b"),
            Trousers(ProductColor.Navy, 5, "c"),
            Sneaker(ProductColor.White, 2, "d"),
        ];

        // Red with purple is no pair; the navy trousers are three steps more formal than the tee.
        Assert.Equal(0, OutfitCounter.Count(wardrobe));
    }

    [Fact]
    public void ANewPieceUnlocksTheOutfitsItCompletes()
    {
        IReadOnlyList<OutfitPiece> wardrobe =
        [
            Jeans(ProductColor.Blue, 2, "a"),
            Trousers(ProductColor.Beige, 3, "b"),
            Sneaker(ProductColor.White, 2, "c"),
            Loafer(ProductColor.Brown, 3, "d"),
        ];

        Assert.Equal(0, OutfitCounter.Count(wardrobe));
        Assert.Equal(4, OutfitCounter.CountWith(wardrobe, Shirt(ProductColor.White, 3, "new")));

        // A layer unlocks the outfits it can be worn over, which needs tops.
        Assert.Equal(0, OutfitCounter.CountWith(wardrobe, Coat(ProductColor.Black, 3, "coat")));
        var withTop = wardrobe.Append(Shirt(ProductColor.White, 3, "top")).ToList();
        Assert.Equal(4, OutfitCounter.CountWith(withTop, Coat(ProductColor.Black, 3, "coat")));
        Assert.Equal(0, OutfitCounter.CountWith(withTop, Piece("aksesuar/canta", ProductColor.Black, 3)));
    }

    [Fact]
    public void TheCountIsCappedForDisplay()
    {
        var colours = new[] { ProductColor.White, ProductColor.Black, ProductColor.Grey, ProductColor.Beige, ProductColor.Navy };
        var wardrobe = new List<OutfitPiece>();
        for (var index = 0; index < 15; index++)
        {
            wardrobe.Add(Tee(colours[index % 5], 2, $"top-{index}"));
            wardrobe.Add(Jeans(ProductColor.Blue, 2, $"bottom-{index}"));
            wardrobe.Add(Sneaker(colours[index % 5], 2, $"shoes-{index}"));
        }

        Assert.Equal(OutfitCounter.DisplayCap, OutfitCounter.Count(wardrobe));
    }

    [Fact]
    public void TheRuleScoreIgnoresPersonaAndWeather()
    {
        var score = OutfitCounter.RuleScore(Tee(ProductColor.Red), Jeans(), Sneaker(ProductColor.White));

        Assert.NotNull(score);
        Assert.InRange(score.Value, OutfitScoring.AcceptanceThreshold, 100);
        Assert.Null(OutfitCounter.RuleScore(Tee(ProductColor.Red, fit: ProductFit.Oversized), Trousers(ProductColor.Navy, 2, fit: ProductFit.Oversized), Sneaker(ProductColor.White)));
    }
}
