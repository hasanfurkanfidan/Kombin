using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using static Kombin.Api.Tests.OutfitPieces;

namespace Kombin.Api.Tests;

public class BudgetSearchTests
{
    private static readonly IReadOnlySet<string> NoExclusions = new HashSet<string>();

    /// <summary>A small owned wardrobe: two tops, one bottom, no shoes, so every outfit has to buy at least the shoes.</summary>
    private static readonly IReadOnlyList<OutfitPiece> Owned =
    [
        Tee(ProductColor.White, 2, "own-tee-white"),
        Tee(ProductColor.Black, 2, "own-tee-black"),
        Jeans(ProductColor.Blue, 2, "own-jeans"),
    ];

    /// <summary>A catalog with every core slot at several prices, neutral colours so the colour rules let most combinations through.</summary>
    private static readonly IReadOnlyList<OutfitPiece> Catalog =
    [
        .. new[] { 150m, 300m, 600m, 1200m, 2400m }.SelectMany((price, index) => new[]
        {
            Piece("ust-giyim/tisort", index % 2 == 0 ? ProductColor.Grey : ProductColor.White, 2, name: $"cat-tee-{price}", source: PieceSource.Catalog, price: price),
            Piece("alt-giyim/jean", index % 2 == 0 ? ProductColor.Black : ProductColor.Blue, 2, fit: ProductFit.Slim, name: $"cat-jeans-{price}", source: PieceSource.Catalog, price: price),
            Piece("ayakkabi/sneaker", index % 2 == 0 ? ProductColor.White : ProductColor.Black, 2, name: $"cat-sneaker-{price}", source: PieceSource.Catalog, price: price),
        }),
    ];

    private static List<OutfitCandidate> Run(decimal budget, IReadOnlyList<OutfitPiece>? owned = null, IReadOnlyList<OutfitPiece>? catalog = null, OutfitContext? context = null)
    {
        context ??= Context();
        var shortlist = OutfitSlots.Core.SelectMany(slot => BudgetSearch.Shortlist((catalog ?? Catalog).Where(piece => piece.Slot == slot), context)).ToList();

        return OutfitGenerator.Generate(new GenerationRequest(
            owned ?? Owned, shortlist, context, null, BudgetSearch.MaxGaps, NoExclusions, BudgetSearch.CatalogPerSlot, BudgetSearch.Reach(budget)));
    }

    private static OutfitCandidate Candidate(int score, params OutfitPiece[] pieces) =>
        new(pieces, new OutfitEvaluation(true, score, OutfitRule.AllNeutral), OutfitHash.Of(pieces));

    private static OutfitPiece Bought(string name, decimal price, string category = "ayakkabi/sneaker") =>
        Piece(category, ProductColor.White, 2, name: name, source: PieceSource.Catalog, price: price);

    [Theory]
    [InlineData(150)]
    [InlineData(400)]
    [InlineData(900)]
    [InlineData(1500)]
    [InlineData(5000)]
    public void TheBudgetIsNeverExceeded(int budget)
    {
        var pick = BudgetSearch.Pick(Run(budget), budget, BudgetSearch.MaxCount, seed: 0);

        Assert.All(pick.Outfits, outfit =>
        {
            Assert.True(outfit.TotalPrice <= budget, $"{outfit.TotalPrice} > {budget}");
            Assert.True(outfit.TotalPrice == outfit.Candidate.Pieces.Sum(piece => piece.Source == PieceSource.Catalog ? piece.Price!.Value : 0));
            Assert.True(outfit.Candidate.Score >= OutfitScoring.AcceptanceThreshold);
            Assert.True(outfit.BoughtCount >= 1);
        });
    }

    [Fact]
    public void EveryShownSeedFitsTheBudget()
    {
        var acceptable = Run(1500);

        for (var seed = 0; seed < 10; seed++)
        {
            Assert.All(BudgetSearch.Pick(acceptable, 1500, 3, seed).Outfits, outfit => Assert.True(outfit.TotalPrice <= 1500));
        }
    }

    [Fact]
    public void TheWardrobeComesFirstAtAnEqualScore()
    {
        var ownedTop = Tee(ProductColor.White, name: "owned-top");
        var boughtTop = Piece("ust-giyim/tisort", ProductColor.White, name: "bought-top", source: PieceSource.Catalog, price: 100);
        var bottom = Jeans(name: "owned-bottom");
        var shoes = Bought("shoes", 500);

        var withOwned = Candidate(80, ownedTop, bottom, shoes);
        var allBought = Candidate(80, boughtTop, bottom, Bought("other-shoes", 300));

        var pick = BudgetSearch.Pick([allBought, withOwned], 2000, 2, 0);

        Assert.Equal(withOwned.Hash, pick.Outfits[0].Candidate.Hash);
    }

    [Fact]
    public void AtAnEqualScoreAndOwnershipTheCheaperOneLeadsAndAHigherScoreBeatsAnyPrice()
    {
        var top = Tee(ProductColor.White, name: "top");
        var bottom = Jeans(name: "bottom");
        var cheap = Candidate(75, top, bottom, Bought("cheap", 200));
        var dear = Candidate(75, top, bottom, Bought("dear", 900));
        var best = Candidate(82, top, bottom, Bought("best", 1400));

        var order = BudgetSearch.Pick([dear, cheap, best], 2000, 3, 0).Outfits.Select(outfit => outfit.Candidate.Hash).ToList();

        Assert.Equal([best.Hash, cheap.Hash, dear.Hash], order);
    }

    [Fact]
    public void TwoShownOutfitsNeverShareMoreThanHalfTheirBoughtPieces()
    {
        var pick = BudgetSearch.Pick(Run(5000, owned: []), 5000, BudgetSearch.MaxCount, 0);
        Assert.True(pick.Outfits.Count >= 2);

        foreach (var left in pick.Outfits)
        {
            foreach (var right in pick.Outfits.Where(other => other != left))
            {
                var shared = left.BoughtKeys.Intersect(right.BoughtKeys).Count();
                Assert.True(2 * shared <= Math.Min(left.BoughtCount, right.BoughtCount), $"{shared} of {left.BoughtCount}/{right.BoughtCount} shared");
            }
        }
    }

    [Fact]
    public void AnOutfitBelowTheFloorIsNeverShownEvenWhenItIsTheOnlyOneThatFits()
    {
        var cheapButPoor = new OutfitCandidate(
            [Tee(ProductColor.White, name: "t"), Jeans(name: "j"), Bought("s", 100)],
            new OutfitEvaluation(true, OutfitScoring.AcceptanceThreshold - 1, OutfitRule.AllNeutral),
            "poor");

        var pick = BudgetSearch.Pick([cheapButPoor], 1000, 3, 0);

        Assert.Empty(pick.Outfits);
        Assert.Null(pick.Closest);
    }

    [Fact]
    public void AnOutfitThatBuysNothingIsNotABudgetOutfit()
    {
        var ownedOnly = Candidate(90, Tee(ProductColor.White, name: "t"), Jeans(name: "j"), Sneaker(ProductColor.White, name: "s"));

        Assert.Empty(BudgetSearch.Pick([ownedOnly], 1000, 3, 0).Outfits);
    }

    [Fact]
    public void WhenNothingFitsTheClosestOutfitWithinReachComesBackAlone()
    {
        var top = Tee(ProductColor.White, name: "top");
        var bottom = Jeans(name: "bottom");
        var near = Candidate(70, top, bottom, Bought("near", 1200));
        var nearer = Candidate(65, top, bottom, Bought("nearer", 1100));
        var far = Candidate(95, top, bottom, Bought("far", 1600));

        var pick = BudgetSearch.Pick([near, nearer, far], 1000, 3, 0);

        Assert.Empty(pick.Outfits);
        Assert.Equal(nearer.Hash, pick.Closest!.Candidate.Hash);
        Assert.Equal(1100, pick.Closest.TotalPrice);

        // Beyond 1.5 × the budget nothing is "close".
        Assert.Null(BudgetSearch.Pick([far], 1000, 3, 0).Closest);
    }

    [Fact]
    public void TheClosestOutfitIsOfferedOnlyWhenNothingFits()
    {
        var top = Tee(ProductColor.White, name: "top");
        var bottom = Jeans(name: "bottom");
        var fits = Candidate(61, top, bottom, Bought("fits", 900));
        var over = Candidate(95, top, bottom, Bought("over", 1100));

        var pick = BudgetSearch.Pick([fits, over], 1000, 3, 0);

        Assert.Single(pick.Outfits);
        Assert.Null(pick.Closest);
    }

    [Fact]
    public void TheSameInputsGiveTheSameOutfitsAndTheNextSeedShowsOthers()
    {
        var first = BudgetSearch.Pick(Run(3000), 3000, 3, 0).Outfits.Select(outfit => outfit.Candidate.Hash).ToList();
        var again = BudgetSearch.Pick(Run(3000), 3000, 3, 0).Outfits.Select(outfit => outfit.Candidate.Hash).ToList();
        var next = BudgetSearch.Pick(Run(3000), 3000, 3, 1).Outfits.Select(outfit => outfit.Candidate.Hash).ToList();

        Assert.Equal(first, again);
        Assert.NotEqual(first, next);
    }

    [Fact]
    public void TheShortlistKeepsCheapAndExpensivePiecesOfEachSlot()
    {
        var shoes = Enumerable.Range(1, 40).Select(index => Piece("ayakkabi/sneaker", ProductColor.White, 2, name: $"s{index}", source: PieceSource.Catalog, price: index * 100m)).ToList();

        var kept = BudgetSearch.Shortlist(shoes, Context());

        Assert.Equal(BudgetSearch.CatalogPerSlot, kept.Count);
        Assert.Contains(kept, piece => piece.Price <= 1000);
        Assert.Contains(kept, piece => piece.Price >= 3100);
    }

    [Fact]
    public void TheShortlistDropsWhatTheDayRulesOut()
    {
        var hot = WeatherRules.From(new Features.Recommendation.Weather.WeatherSnapshot("Hot", Day, 25, 34, 0, 0, 0));
        var boots = Enumerable.Range(1, 5).Select(index => Piece("ayakkabi/bot", ProductColor.Black, 3, name: $"b{index}", source: PieceSource.Catalog, price: 500)).ToList();

        Assert.Empty(BudgetSearch.Shortlist(boots, Context(weather: hot)));
    }

    [Fact]
    public void TheCostCapRejectsEveryCombinationAboveIt()
    {
        var acceptable = OutfitGenerator.Generate(new GenerationRequest(Owned, Catalog, Context(), null, BudgetSearch.MaxGaps, NoExclusions, 20, CostCap: 700));

        Assert.NotEmpty(acceptable);
        Assert.All(acceptable, candidate => Assert.True(candidate.Pieces.Sum(piece => OutfitGenerator.Cost(piece, null)) <= 700));
    }

    [Fact]
    public void AnOfferInTheCallersSizeWinsOverACheaperOneInAnother()
    {
        var offers = new[]
        {
            Offer("cheap-s", 300, ["S"]),
            Offer("dear-m", 450, ["M", "L"]),
            Offer("unlisted", 350, []),
        };

        var chosen = BudgetOffers.Choose(offers, "m")!;

        Assert.Equal(IdOf("dear-m"), chosen.OfferId);
        Assert.Equal("M", chosen.Size);
        Assert.False(chosen.SizeUnknown);
    }

    [Fact]
    public void WithoutAnOfferInTheSizeAnOfferListingNoSizesIsTakenAsSizeUnknownAndOthersNever()
    {
        Assert.True(BudgetOffers.Choose([Offer("s-only", 300, ["S"]), Offer("unlisted", 350, [])], "M") is { SizeUnknown: true, Size: null } unlisted
            && unlisted.OfferId == IdOf("unlisted"));
        Assert.Null(BudgetOffers.Choose([Offer("s-only", 300, ["S"])], "M"));
    }

    [Fact]
    public void WithoutAKnownSizeTheCheapestOfferIsTakenAsSizeUnknown()
    {
        var chosen = BudgetOffers.Choose([Offer("dear", 500, ["M"]), Offer("cheap", 200, ["S"])], null)!;

        Assert.Equal(IdOf("cheap"), chosen.OfferId);
        Assert.True(chosen.SizeUnknown);
    }

    [Fact]
    public void SavingsExistOnlyForARealCrossedOutPrice()
    {
        Assert.Equal(100, new BudgetOffer(Guid.NewGuid(), 400, 400, 500, null, true).Savings);
        Assert.Null(new BudgetOffer(Guid.NewGuid(), 400, 400, 400, null, true).Savings);
        Assert.Null(new BudgetOffer(Guid.NewGuid(), 400, 400, null, null, true).Savings);
    }

    [Fact]
    public void TheCallersOwnSizeForTheBrandBeatsTheInferredOne()
    {
        var brand = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var entries = new[] { (brand, "S", now), (Guid.NewGuid(), "M", now), (Guid.NewGuid(), "M", now) };

        Assert.Equal("S", BudgetOffers.SizeFor(entries, brand));
        Assert.Equal("M", BudgetOffers.SizeFor(entries, Guid.NewGuid()));
        Assert.Null(BudgetOffers.SizeFor([(Guid.NewGuid(), "M", now)], Guid.NewGuid()));
    }

    private static OfferOption Offer(string name, decimal price, string[] sizes) => new(IdOf(name), price, price, null, name, sizes);
}
