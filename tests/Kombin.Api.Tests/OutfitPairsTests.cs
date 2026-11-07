using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using static Kombin.Api.Tests.OutfitPieces;

namespace Kombin.Api.Tests;

public class OutfitPairsTests
{
    private static OutfitPiece Oversized(string categorySlug, ProductColor color, int formality) =>
        Piece(categorySlug, color, formality, fit: ProductFit.Oversized);

    private static OutfitPiece Shop(string categorySlug, ProductColor color, int formality, string name, int personaScore = 50, decimal price = 500m) =>
        Piece(categorySlug, color, formality, name: name, source: PieceSource.Catalog, personaScore: personaScore, price: price);

    // ---- pair rules ----

    [Fact]
    public void ANavyBlazerPairsWithAWhiteShirtAndGreyTrousersButNotWithAnotherOversizedLayer()
    {
        var blazer = Blazer(ProductColor.Navy);

        // Neutrals (0.85), same level, no silhouette to judge (0.85), no pattern: (0.2125 + 0.15 + 0.085 + 0.10) / 0.60.
        Assert.Equal(new PairVerdict(true, 91, PairReason.Neutral), OutfitPairs.Judge(blazer, Shirt(ProductColor.White, 4)));
        Assert.Equal(new PairVerdict(true, 91, PairReason.Neutral), OutfitPairs.Judge(blazer, Trousers(ProductColor.Grey, 4)));

        Assert.False(OutfitPairs.Judge(blazer, Oversized("dis-giyim/kaban", ProductColor.Beige, 4)).Allowed);
    }

    [Fact]
    public void TheReasonNamesTheColourHarmony()
    {
        Assert.Equal(PairReason.Neutral, OutfitPairs.Judge(Tee(ProductColor.White), Jeans()).Reason);
        Assert.Equal(PairReason.Neutral, OutfitPairs.Judge(Tee(ProductColor.Black), Trousers(ProductColor.Navy, 2)).Reason);
        Assert.Equal(PairReason.Statement, OutfitPairs.Judge(Tee(ProductColor.Red), Jeans()).Reason);
        Assert.Equal(PairReason.Tonal, OutfitPairs.Judge(Tee(ProductColor.Red), Trousers(ProductColor.Red, 2)).Reason);
        Assert.Equal(PairReason.Analogous, OutfitPairs.Judge(Tee(ProductColor.Red), Trousers(ProductColor.Orange, 2)).Reason);
        Assert.Equal(PairReason.Complementary, OutfitPairs.Judge(Tee(ProductColor.Blue), Trousers(ProductColor.Orange, 2)).Reason);
    }

    [Fact]
    public void TheScoreComesFromTheRuleTables()
    {
        // A statement colour on slim jeans: every component at its best.
        Assert.Equal(100, OutfitPairs.Judge(Tee(ProductColor.Red), Jeans()).Score);

        // Tonal (0.95) and regular on regular (0.9): (0.2375 + 0.15 + 0.09 + 0.10) / 0.60.
        Assert.Equal(96, OutfitPairs.Judge(Tee(ProductColor.Red), Trousers(ProductColor.Red, 2)).Score);

        // A quiet neutral pair scores below a plain one, and a step of formality below the same level.
        Assert.True(OutfitPairs.Judge(Tee(ProductColor.Black), Trousers(ProductColor.Navy, 2)).Score < OutfitPairs.Judge(Tee(ProductColor.White), Trousers(ProductColor.Navy, 2)).Score);
        Assert.True(OutfitPairs.Judge(Tee(ProductColor.White, 3), Trousers(ProductColor.Navy, 2)).Score < OutfitPairs.Judge(Tee(ProductColor.White, 2), Trousers(ProductColor.Navy, 2)).Score);
    }

    [Fact]
    public void WhatTheOutfitRulesRejectIsNoPair()
    {
        // Two steps of formality, for the core and for a layer.
        Assert.False(OutfitPairs.Judge(Tee(ProductColor.White, 2), Trousers(ProductColor.Grey, 4)).Allowed);
        Assert.False(OutfitPairs.Judge(Blazer(ProductColor.Navy, 4), Tee(ProductColor.White, 2)).Allowed);

        // Two colours that are no known pair; a print with a colour.
        Assert.False(OutfitPairs.Judge(Tee(ProductColor.Red), Trousers(ProductColor.Blue, 2)).Allowed);
        Assert.False(OutfitPairs.Judge(Tee(ProductColor.Multi), Trousers(ProductColor.Red, 2)).Allowed);

        // Two strong patterns; volume on both halves.
        Assert.False(OutfitPairs.Judge(Tee(ProductColor.White, pattern: ProductPattern.Printed), Trousers(ProductColor.Grey, 2, pattern: ProductPattern.Checked)).Allowed);
        Assert.False(OutfitPairs.Judge(Tee(ProductColor.White, fit: ProductFit.Oversized), Trousers(ProductColor.Grey, 2, fit: ProductFit.Oversized)).Allowed);

        // Two pieces of one slot are alternatives.
        Assert.False(OutfitPairs.Judge(Tee(ProductColor.White, 3), Shirt(ProductColor.Blue)).Allowed);
    }

    [Fact]
    public void AJudgementDoesNotDependOnWhichPieceWasTapped()
    {
        var top = Tee(ProductColor.Red, fit: ProductFit.Oversized);
        var bottom = Trousers(ProductColor.Grey, 2, fit: ProductFit.Relaxed);

        Assert.Equal(OutfitPairs.Judge(top, bottom), OutfitPairs.Judge(bottom, top));
    }

    [Fact]
    public void ALayerMayWearOneStepAboveThePieceUnderIt()
    {
        var verdict = OutfitPairs.Judge(Blazer(ProductColor.Navy, 4), Shirt(ProductColor.White, 3));

        Assert.True(verdict.Allowed);
        Assert.Equal(91, verdict.Score);
    }

    // ---- companions ----

    [Fact]
    public void CompanionsAreGroupedBySlotBestFirstWithoutTheItemsOwnSlot()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        OutfitPiece[] wardrobe =
        [
            shirt,
            Tee(ProductColor.Grey, 3, "other-top"),
            Trousers(ProductColor.Black, 3, "regular"),
            Jeans(ProductColor.Blue, 3, "slim"),
            Trousers(ProductColor.Grey, 5, "too-formal"),
            Loafer(ProductColor.Brown, 3, "loafer"),
        ];

        var groups = OutfitPairs.Companions(shirt, wardrobe, 6, weighPersona: false);

        Assert.Equal([OutfitSlot.Bottom, OutfitSlot.Outerwear, OutfitSlot.Shoes, OutfitSlot.Accessory], groups.Select(group => group.Slot));

        var bottoms = groups[0];
        Assert.Equal(2, bottoms.Total);
        Assert.Equal([IdOf("slim"), IdOf("regular")], bottoms.Companions.Select(companion => companion.Piece.Id));
        Assert.True(bottoms.Companions[0].Score > bottoms.Companions[1].Score);

        Assert.Empty(groups[1].Companions);
        Assert.Equal(1, groups[2].Total);
        Assert.Equal(0, groups[3].Total);
    }

    [Fact]
    public void TheLimitIsPerSlotAndTheTotalStillCountsThemAll()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        OutfitPiece[] wardrobe = [shirt, Trousers(ProductColor.Black, 3, "one"), Trousers(ProductColor.Navy, 3, "two"), Trousers(ProductColor.Beige, 3, "three"), Loafer(ProductColor.Brown, 3, "shoe")];

        var groups = OutfitPairs.Companions(shirt, wardrobe, 2, weighPersona: false);

        var bottoms = groups.Single(group => group.Slot == OutfitSlot.Bottom);
        Assert.Equal(3, bottoms.Total);
        Assert.Equal(2, bottoms.Companions.Count);
        Assert.Single(groups.Single(group => group.Slot == OutfitSlot.Shoes).Companions);
    }

    [Fact]
    public void APersonaReordersEqualPairsButExcludesNothing()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        var liked = Piece("alt-giyim/pantolon", ProductColor.Navy, 3, name: "liked", personaScore: 100);
        var disliked = Piece("alt-giyim/pantolon", ProductColor.Grey, 3, name: "disliked", personaScore: 0);
        OutfitPiece[] wardrobe = [shirt, disliked, liked];
        var ruleScore = OutfitPairs.Judge(shirt, liked).Score;

        var weighted = OutfitPairs.Companions(shirt, wardrobe, 6, weighPersona: true).Single(group => group.Slot == OutfitSlot.Bottom).Companions;

        Assert.Equal(["liked", "disliked"], weighted.Select(companion => companion.Piece.Id == liked.Id ? "liked" : "disliked"));
        Assert.Equal(OutfitScoring.WithPersona(ruleScore, 100), weighted[0].Score);
        Assert.Equal(OutfitScoring.WithPersona(ruleScore, 0), weighted[1].Score);

        var plain = OutfitPairs.Companions(shirt, wardrobe, 6, weighPersona: false).Single(group => group.Slot == OutfitSlot.Bottom).Companions;
        Assert.All(plain, companion => Assert.Equal(ruleScore, companion.Score));
    }

    [Fact]
    public void ThePersonaWeighsInAtItsShareOfTheOutfitScore()
    {
        // (0.60 * 91 + 0.20 * 100) / 0.80.
        Assert.Equal(93, OutfitScoring.WithPersona(91, 100));
        Assert.Equal(70, OutfitScoring.WithPersona(70, 70));
    }

    [Fact]
    public void CompanionCountsAgreeWithTheCompanionsOfEachPiece()
    {
        OutfitPiece[] wardrobe =
        [
            Blazer(ProductColor.Navy, 4),
            Shirt(ProductColor.White, 4),
            Tee(ProductColor.Red, 2),
            Trousers(ProductColor.Grey, 4),
            Jeans(),
            Loafer(ProductColor.Black, 4),
            Sneaker(ProductColor.White, 2),
            Piece("dis-giyim/kaban", ProductColor.Beige, 4, fit: ProductFit.Oversized),
            Piece("aksesuar/kemer", ProductColor.Brown, 3),
        ];

        var counts = OutfitPairs.CountCompanions(wardrobe);

        Assert.Equal(wardrobe.Length, counts.Count);
        Assert.All(wardrobe, piece => Assert.Equal(OutfitPairs.Companions(piece, wardrobe, 6, weighPersona: false).Sum(group => group.Total), counts[piece.Id]));
        Assert.True(counts[wardrobe[0].Id] > 0);
    }

    [Fact]
    public void ALonePieceHasNoCompanions()
    {
        var tee = Tee(ProductColor.White);

        Assert.Equal(0, OutfitPairs.CountCompanions([tee])[tee.Id]);
        Assert.Empty(OutfitPairs.CountCompanions([]));
    }

    // ---- counting outfits around one piece ----

    [Fact]
    public void AnItemsOutfitsAddUpToThePossibleOutfits()
    {
        OutfitPiece[] wardrobe =
        [
            Tee(ProductColor.White, 2, "tee-white"),
            Tee(ProductColor.Red, 2, "tee-red"),
            Shirt(ProductColor.Blue, 3, "shirt-blue"),
            Jeans(ProductColor.Blue, 2, "jeans"),
            Trousers(ProductColor.Navy, 3, "trousers"),
            Trousers(ProductColor.Grey, 5, "suit"),
            Sneaker(ProductColor.White, 2, "sneaker"),
            Loafer(ProductColor.Brown, 3, "loafer"),
            Jacket(ProductColor.Navy, 3, "layer"),
        ];

        var total = OutfitCounter.Count(wardrobe);
        int Sum(OutfitSlot slot) => wardrobe.Where(piece => piece.Slot == slot).Sum(piece => OutfitCounter.CountIncluding(wardrobe, [piece]));

        Assert.True(total > 0);
        Assert.Equal(total, Sum(OutfitSlot.Top));
        Assert.Equal(total, Sum(OutfitSlot.Bottom));
        Assert.Equal(total, Sum(OutfitSlot.Shoes));
    }

    [Fact]
    public void ALayerAndAnAccessoryCountTheCoreOutfitsTheyCanBeWornWith()
    {
        OutfitPiece[] core = [Tee(ProductColor.White), Jeans(), Sneaker(ProductColor.White)];
        var jacket = Jacket(ProductColor.Navy, 3);
        var belt = Piece("aksesuar/kemer", ProductColor.Brown, 2);

        Assert.Equal(1, OutfitCounter.CountIncluding(core, [jacket]));
        Assert.Equal(OutfitCounter.CountWith(core, jacket), OutfitCounter.CountIncluding(core, [jacket]));
        Assert.Equal(1, OutfitCounter.CountIncluding(core, [belt]));

        // The accessory belongs to the core: two steps away it fits no outfit. As a purchase it still unlocks nothing.
        Assert.Equal(0, OutfitCounter.CountIncluding(core, [Piece("aksesuar/kemer", ProductColor.Brown, 4)]));
        Assert.Equal(0, OutfitCounter.CountWith(core, belt));
    }

    // ---- unlock ----

    [Fact]
    public void TheSuggestionGoesToTheEmptiestSlot()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        OutfitPiece[] wardrobe = [shirt, Trousers(ProductColor.Navy, 3, "bottom"), Jacket(ProductColor.Navy, 3, "first"), Coat(ProductColor.Black, 3, "second")];

        var slots = OutfitPairs.UnlockSlots(shirt, OutfitPairs.Companions(shirt, wardrobe, 6, weighPersona: false));

        Assert.Equal([OutfitSlot.Shoes, OutfitSlot.Bottom, OutfitSlot.Outerwear], slots);
    }

    [Fact]
    public void EquallyEmptySlotsTryTheCoreBeforeTheLayerAndNeverTheItemsOwn()
    {
        var trousers = Trousers(ProductColor.Navy, 3, "bottom");

        Assert.Equal([OutfitSlot.Top, OutfitSlot.Shoes, OutfitSlot.Outerwear], OutfitPairs.UnlockSlots(trousers, OutfitPairs.Companions(trousers, [trousers], 6, weighPersona: false)));
    }

    [Fact]
    public void ThreeCompanionsInEverySlotNeedNoSuggestionAndAccessoriesDoNotCount()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        OutfitPiece[] wardrobe =
        [
            shirt,
            Trousers(ProductColor.Navy, 3, "b1"), Trousers(ProductColor.Black, 3, "b2"), Trousers(ProductColor.Beige, 3, "b3"),
            Loafer(ProductColor.Brown, 3, "s1"), Loafer(ProductColor.Black, 3, "s2"), Boot(ProductColor.Black, 3, "s3"),
            Jacket(ProductColor.Navy, 3, "o1"), Coat(ProductColor.Black, 3, "o2"), Coat(ProductColor.Beige, 3, "o3"),
        ];

        Assert.Empty(OutfitPairs.UnlockSlots(shirt, OutfitPairs.Companions(shirt, wardrobe, 6, weighPersona: false)));
    }

    [Fact]
    public void TheEstimateIsWhatTheItemGains()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        OutfitPiece[] wardrobe = [shirt, Trousers(ProductColor.Navy, 3, "first"), Trousers(ProductColor.Beige, 3, "second"), Trousers(ProductColor.Grey, 5, "too-formal")];
        var loafer = Shop("ayakkabi/loafer", ProductColor.Black, 3, "loafer");

        var unlock = OutfitPairs.PickUnlock(shirt, wardrobe, [loafer], minMatchScore: null);

        Assert.NotNull(unlock);
        Assert.Equal(loafer, unlock.Piece);
        Assert.Equal(0, OutfitCounter.CountIncluding(wardrobe, [shirt]));
        Assert.Equal(OutfitCounter.CountIncluding([.. wardrobe, loafer], [shirt]), unlock.UnlocksEstimate);
        Assert.Equal(2, unlock.UnlocksEstimate);
    }

    [Fact]
    public void ANeutralWinsEvenWhenAColourWouldUnlockMore()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        OutfitPiece[] wardrobe = [shirt, Trousers(ProductColor.Navy, 3, "smart"), Trousers(ProductColor.Grey, 4, "formal")];
        var neutral = Shop("ayakkabi/sneaker", ProductColor.Black, 2, "neutral", price: 900m);
        var colour = Shop("ayakkabi/loafer", ProductColor.Red, 3, "colour", price: 100m);

        Assert.True(OutfitCounter.CountIncluding(wardrobe, [shirt, colour]) > OutfitCounter.CountIncluding(wardrobe, [shirt, neutral]));
        Assert.Equal(neutral, OutfitPairs.PickUnlock(shirt, wardrobe, [colour, neutral], minMatchScore: null)?.Piece);
    }

    [Fact]
    public void AmongNeutralsTheMostOutfitsWinThenTheMatchThenThePrice()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        OutfitPiece[] wardrobe = [shirt, Trousers(ProductColor.Navy, 3, "smart"), Trousers(ProductColor.Grey, 4, "formal")];
        var fewer = Shop("ayakkabi/sneaker", ProductColor.Black, 2, "fewer", personaScore: 100, price: 100m);
        var more = Shop("ayakkabi/loafer", ProductColor.Black, 3, "more", personaScore: 70, price: 800m);
        var moreLiked = Shop("ayakkabi/loafer", ProductColor.Brown, 3, "more-liked", personaScore: 90, price: 900m);
        var moreLikedCheaper = Shop("ayakkabi/loafer", ProductColor.Beige, 3, "more-liked-cheaper", personaScore: 90, price: 400m);

        Assert.Equal(more, OutfitPairs.PickUnlock(shirt, wardrobe, [fewer, more], null)?.Piece);
        Assert.Equal(moreLiked, OutfitPairs.PickUnlock(shirt, wardrobe, [fewer, more, moreLiked], null)?.Piece);
        Assert.Equal(moreLikedCheaper, OutfitPairs.PickUnlock(shirt, wardrobe, [fewer, more, moreLiked, moreLikedCheaper], null)?.Piece);
    }

    [Fact]
    public void WithAPersonaACandidateMustMatchItWell()
    {
        var shirt = Shirt(ProductColor.White, 3, "shirt");
        var weak = Shop("ayakkabi/loafer", ProductColor.Black, 3, "weak", personaScore: 59);
        var fine = Shop("ayakkabi/loafer", ProductColor.Red, 3, "fine", personaScore: 60);

        Assert.Equal(fine, OutfitPairs.PickUnlock(shirt, [shirt], [weak, fine], minMatchScore: 60)?.Piece);
        Assert.Null(OutfitPairs.PickUnlock(shirt, [shirt], [weak], minMatchScore: 60));
        Assert.Equal(weak, OutfitPairs.PickUnlock(shirt, [shirt], [weak], minMatchScore: null)?.Piece);
    }

    [Fact]
    public void ACandidateThatDoesNotPairWithTheItemIsNoSuggestion()
    {
        var tee = Tee(ProductColor.Red, 2, "tee");

        Assert.Null(OutfitPairs.PickUnlock(tee, [tee], [Shop("ayakkabi/sneaker", ProductColor.Blue, 2, "clash"), Shop("ayakkabi/loafer", ProductColor.Black, 4, "too-formal")], minMatchScore: null));
    }
}
