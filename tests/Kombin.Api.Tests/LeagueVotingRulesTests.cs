using Kombin.Api.Features.League;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Tests;

/// <summary>The repeat rule of <c>docs/design/league.md</c>, section 5: 30 days exact, 7 days one piece apart, accessories ignored.</summary>
public class RepeatRuleTests
{
    private static readonly DateOnly Day = new(2026, 11, 16);

    private static readonly LeagueOptions Options = new();

    private static readonly Guid Shirt = Guid.NewGuid(), Jeans = Guid.NewGuid(), Sneakers = Guid.NewGuid(), Boots = Guid.NewGuid(), Coat = Guid.NewGuid(), Chinos = Guid.NewGuid();

    private static Guid[] Set(params Guid[] ids) => [.. ids.Order()];

    private static PastEntry Competed(int daysAgo, params Guid[] ids) => new(Day.AddDays(-daysAgo), Set(ids), Competed: true);

    private static bool Blocked(Guid[] today, params PastEntry[] earlier) => RepeatRule.IsBlocked(today, Day, earlier, Options);

    [Theory]
    [InlineData(1, true)]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(45, false)]
    public void TheSamePieceSetCompetesOnceInThirtyDays(int daysAgo, bool blocked) =>
        Assert.Equal(blocked, Blocked(Set(Shirt, Jeans, Sneakers), Competed(daysAgo, Sneakers, Jeans, Shirt)));

    [Theory]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void OnePieceSwappedCountsAsTheSameWithinSevenDays(int daysAgo, bool blocked) =>
        Assert.Equal(blocked, Blocked(Set(Shirt, Jeans, Boots), Competed(daysAgo, Shirt, Jeans, Sneakers)));

    [Fact]
    public void OnePieceAddedOrRemovedIsNearToo()
    {
        Assert.True(Blocked(Set(Shirt, Jeans, Sneakers, Coat), Competed(3, Shirt, Jeans, Sneakers)));
        Assert.True(Blocked(Set(Shirt, Jeans), Competed(3, Shirt, Jeans, Sneakers)));
    }

    [Fact]
    public void TwoPiecesApartIsAnotherOutfit() =>
        Assert.False(Blocked(Set(Shirt, Chinos, Boots), Competed(1, Shirt, Jeans, Sneakers)));

    [Fact]
    public void AccessoriesAreLeftOutOfThePieceSet()
    {
        var bag = Guid.NewGuid();
        var belt = Guid.NewGuid();
        var withBag = RepeatRule.PieceIds([(OutfitSlot.Top, Shirt), (OutfitSlot.Bottom, Jeans), (OutfitSlot.Shoes, Sneakers), (OutfitSlot.Accessory, bag)]);
        var withBelt = RepeatRule.PieceIds([(OutfitSlot.Accessory, belt), (OutfitSlot.Shoes, Sneakers), (OutfitSlot.Bottom, Jeans), (OutfitSlot.Top, Shirt)]);

        Assert.Equal(withBag, withBelt);
        Assert.Equal(RepeatRule.Hash(withBag), RepeatRule.Hash(withBelt));
        Assert.Equal(32, RepeatRule.Hash(withBag).Length);
        Assert.True(Blocked(withBelt, new PastEntry(Day.AddDays(-20), withBag, Competed: true)));
    }

    [Fact]
    public void ACatalogPieceIsItsProductSoArchivingAndAddingItAgainIsTheSamePiece()
    {
        var product = Guid.NewGuid();
        var archivedItem = Guid.NewGuid();
        var addedAgain = Guid.NewGuid();
        var photoOfTheSameProduct = Guid.NewGuid();

        var before = RepeatRule.PieceIds([(OutfitSlot.Top, Shirt), (OutfitSlot.Bottom, Jeans), (OutfitSlot.Shoes, RepeatRule.Identity(archivedItem, product, fromCatalog: true))]);
        var after = RepeatRule.PieceIds([(OutfitSlot.Top, Shirt), (OutfitSlot.Bottom, Jeans), (OutfitSlot.Shoes, RepeatRule.Identity(addedAgain, product, fromCatalog: true))]);

        Assert.Equal(before, after);
        Assert.True(Blocked(after, new PastEntry(Day.AddDays(-20), before, Competed: true)));

        // A photo piece keeps its own item ID, even one linked to a product.
        Assert.Equal(photoOfTheSameProduct, RepeatRule.Identity(photoOfTheSameProduct, product, fromCatalog: false));
        Assert.Equal(addedAgain, RepeatRule.Identity(addedAgain, null, fromCatalog: true));
    }

    [Fact]
    public void WithdrawnRemovedOrNotCompetingEntriesBlockNothing() =>
        Assert.False(Blocked(Set(Shirt, Jeans, Sneakers), new PastEntry(Day.AddDays(-1), Set(Shirt, Jeans, Sneakers), Competed: false)));

    [Fact]
    public void TodayAndLaterDaysAreNotEarlierEntries() =>
        Assert.False(Blocked(Set(Shirt, Jeans, Sneakers), Competed(0, Shirt, Jeans, Sneakers), Competed(-1, Shirt, Jeans, Sneakers)));
}

/// <summary>The pairing of <c>docs/design/league.md</c>, section 6.</summary>
public class PairingTests
{
    private static readonly Guid Voter = Guid.NewGuid();

    private static readonly DateOnly Day = new(2026, 11, 16);

    private static readonly HashSet<(Guid, Guid)> Nothing = [];

    private static PairCandidate Human(Audience audience = Audience.Women, Occasion? occasion = Occasion.Office, int exposures = 0) =>
        new(Guid.NewGuid(), true, audience, occasion, exposures);

    private static PairCandidate Engine(Audience audience = Audience.Women, Occasion? occasion = Occasion.Office, int exposures = 0) =>
        new(Guid.NewGuid(), false, audience, occasion, exposures);

    private static (Guid, Guid) Ids(PairChoice choice) => Pairing.Key(choice.LeftId, choice.RightId);

    [Fact]
    public void TheMatchIsRelaxedOccasionFirstThenAudience()
    {
        var first = Human(exposures: 0);
        var sameBoth = Human(exposures: 5);
        var sameAudience = Human(occasion: Occasion.Evening, exposures: 1);
        var otherAudience = Human(audience: Audience.Men, exposures: 1);

        var strict = Pairing.Choose([first, sameBoth, sameAudience, otherAudience], Nothing, Voter, Day, 1)!;
        var relaxed = Pairing.Choose([first, sameAudience, otherAudience], Nothing, Voter, Day, 1)!;
        var any = Pairing.Choose([first, otherAudience], Nothing, Voter, Day, 1)!;

        Assert.Equal((LeaguePairMatch.Strict, Pairing.Key(first.Id, sameBoth.Id)), (strict.Level, Ids(strict)));
        Assert.Equal(LeaguePairMatch.SameAudience, relaxed.Level);
        Assert.Contains(sameAudience.Id, new[] { relaxed.LeftId, relaxed.RightId });
        Assert.Equal((LeaguePairMatch.Any, Pairing.Key(first.Id, otherAudience.Id)), (any.Level, Ids(any)));
    }

    [Fact]
    public void ANullOccasionMatchesOnlyANullOccasionStrictly()
    {
        var noOccasion = Human(occasion: null);
        var office = Human(occasion: Occasion.Office, exposures: 1);
        var alsoNone = Human(occasion: null, exposures: 3);

        var choice = Pairing.Choose([noOccasion, office, alsoNone], Nothing, Voter, Day, 1)!;

        Assert.Equal(LeaguePairMatch.Strict, choice.Level);
        Assert.Equal(Pairing.Key(noOccasion.Id, alsoNone.Id), Ids(choice));
    }

    [Fact]
    public void TheLeastExposedOutfitsArePairedFirst()
    {
        var shown = Human(exposures: 9);
        var fresh = Human(exposures: 0);
        var once = Human(exposures: 1);

        var choice = Pairing.Choose([shown, fresh, once], Nothing, Voter, Day, 1)!;

        Assert.Equal(Pairing.Key(fresh.Id, once.Id), Ids(choice));
    }

    [Fact]
    public void APairIsNeverServedTwiceToTheSameVoter()
    {
        var first = Human(exposures: 0);
        var second = Human(exposures: 1);
        var third = Human(exposures: 2);

        var served = new HashSet<(Guid, Guid)> { Pairing.Key(first.Id, second.Id) };
        var choice = Pairing.Choose([first, second, third], served, Voter, Day, 2)!;
        Assert.Equal(Pairing.Key(first.Id, third.Id), Ids(choice));

        served.Add(Pairing.Key(first.Id, third.Id));
        Assert.Equal(Pairing.Key(second.Id, third.Id), Ids(Pairing.Choose([first, second, third], served, Voter, Day, 3)!));

        served.Add(Pairing.Key(second.Id, third.Id));
        Assert.Null(Pairing.Choose([first, second, third], served, Voter, Day, 4));
    }

    [Fact]
    public void OneSideIsAlwaysHuman()
    {
        var human = Human(exposures: 4);
        var engines = Enumerable.Range(0, 5).Select(_ => Engine(exposures: 0)).ToList();

        Assert.Null(Pairing.Choose([.. engines], Nothing, Voter, Day, 1));

        var choice = Pairing.Choose([human, .. engines], Nothing, Voter, Day, 1)!;
        Assert.Contains(human.Id, new[] { choice.LeftId, choice.RightId });
        Assert.Contains(choice.LeftId == human.Id ? choice.RightId : choice.LeftId, engines.Select(engine => engine.Id));
    }

    [Fact]
    public void AnOutfitWithoutAPartnerIsSkippedForTheNextOne()
    {
        var lonely = Human(exposures: 0);
        var a = Human(exposures: 1);
        var b = Human(exposures: 1);
        var served = new HashSet<(Guid, Guid)> { Pairing.Key(lonely.Id, a.Id), Pairing.Key(lonely.Id, b.Id) };

        var choice = Pairing.Choose([lonely, a, b], served, Voter, Day, 3)!;

        Assert.Equal(Pairing.Key(a.Id, b.Id), Ids(choice));
    }

    [Fact]
    public void TheSameInputsGiveTheSamePairAndTiesSpreadOverVoters()
    {
        var candidates = Enumerable.Range(0, 20).Select(_ => Human()).ToList();

        Assert.Equal(Pairing.Choose(candidates, Nothing, Voter, Day, 1), Pairing.Choose(candidates, Nothing, Voter, Day, 1));

        var firsts = Enumerable.Range(0, 30)
            .Select(_ => Pairing.Choose(candidates, Nothing, Guid.NewGuid(), Day, 1)!)
            .Select(Ids)
            .Distinct()
            .Count();
        Assert.True(firsts > 5, $"30 voters received only {firsts} different first pairs among equal outfits.");
    }
}

/// <summary>The chosen outfit's record after a vote (section 7): the vote itself included, no rate below 5 games, nothing for a skip.</summary>
public class WinRateTests
{
    [Fact]
    public void TheRateIncludesThisVoteAndIsRoundedToAWholePercent()
    {
        // 18 weighted games, 13 won (this vote among them): 72 %.
        Assert.Equal(new ChosenRecord(72, 18), WinRate.For(13, 18, skipped: false, minGames: 5));
        Assert.Equal(new ChosenRecord(67, 6), WinRate.For(4, 6, skipped: false, minGames: 5));
        Assert.Equal(new ChosenRecord(100, 5), WinRate.For(5, 5, skipped: false, minGames: 5));
    }

    [Fact]
    public void HalfWeightedVotesAreCountedByWeight()
    {
        // Ten new voters' half votes are five games: the rate shows, 3.5 of 5 won.
        Assert.Equal(new ChosenRecord(70, 5), WinRate.For(3.5, 5, skipped: false, minGames: 5));
        Assert.Equal(new ChosenRecord(null, 4), WinRate.For(4.5, 4.5, skipped: false, minGames: 5));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    public void BelowFiveGamesThereIsNoRateOnlyTheGames(double wins, double games)
    {
        var record = WinRate.For(wins, games, skipped: false, minGames: 5);

        Assert.Null(record.WinRate);
        Assert.Equal((int)games, record.Games);
    }

    [Fact]
    public void ASkipChoseNothing() => Assert.Equal(new ChosenRecord(null, null), WinRate.For(10, 20, skipped: true, minGames: 5));
}

/// <summary>The eligibility order of section 5: the first failure is the reason.</summary>
public class EntryEligibilityTests
{
    private static EligibilityPiece Piece(OutfitSlot slot = OutfitSlot.Top, ModerationVerdict? verdict = ModerationVerdict.Allow, bool archived = false, bool owned = true, bool found = true, bool image = true) =>
        new(slot, owned ? Guid.NewGuid() : null, found, archived, verdict, image);

    private static EligibilityPiece[] Outfit(params EligibilityPiece[] extra) => [Piece(OutfitSlot.Top), Piece(OutfitSlot.Bottom), .. extra];

    [Fact]
    public void AnOutfitOfAllowedPiecesCompetes() =>
        Assert.Equal(EntryVerdict.Competing, EntryEligibility.Evaluate(false, Outfit(Piece(OutfitSlot.Shoes)), 2, false));

    [Fact]
    public void TheFirstFailureIsTheReason()
    {
        var everythingWrong = Outfit(Piece(OutfitSlot.Shoes, owned: false), Piece(OutfitSlot.Outerwear, archived: true), Piece(OutfitSlot.Shoes, verdict: ModerationVerdict.Block));

        Assert.Equal(LeagueEntryReason.AgeRestricted, EntryEligibility.Evaluate(true, everythingWrong, 2, true).Reason);
        Assert.Equal(LeagueEntryReason.IncompleteOutfit, EntryEligibility.Evaluate(false, everythingWrong, 2, true).Reason);
        Assert.Equal(LeagueEntryReason.PieceUnavailable, EntryEligibility.Evaluate(false, Outfit(Piece(archived: true), Piece(verdict: ModerationVerdict.Block)), 2, true).Reason);
        Assert.Equal(LeagueEntryReason.RecentlyCompeted, EntryEligibility.Evaluate(false, Outfit(Piece(verdict: ModerationVerdict.Block)), 2, true).Reason);
        Assert.Equal(LeagueEntryReason.UnsafePiece, EntryEligibility.Evaluate(false, Outfit(Piece(verdict: ModerationVerdict.Review), Piece(verdict: null)), 2, false).Reason);
    }

    [Fact]
    public void AnOutfitNeedsTwoNonAccessoryWardrobePieces()
    {
        Assert.Equal(LeagueEntryReason.IncompleteOutfit, EntryEligibility.Evaluate(false, [Piece(OutfitSlot.Top), Piece(OutfitSlot.Accessory)], 2, false).Reason);
        Assert.Equal(LeagueEntryReason.IncompleteOutfit, EntryEligibility.Evaluate(false, Outfit(Piece(OutfitSlot.Shoes, owned: false)), 2, false).Reason);
    }

    [Fact]
    public void AMissingPieceOrImageMakesItUnavailable()
    {
        Assert.Equal(LeagueEntryReason.PieceUnavailable, EntryEligibility.Evaluate(false, Outfit(Piece(found: false)), 2, false).Reason);
        Assert.Equal(LeagueEntryReason.PieceUnavailable, EntryEligibility.Evaluate(false, Outfit(Piece(image: false)), 2, false).Reason);
    }

    [Fact]
    public void APieceNotYetModeratedKeepsTheEntryWaiting() =>
        Assert.Equal(EntryVerdict.Pending, EntryEligibility.Evaluate(false, Outfit(Piece(verdict: null)), 2, false));
}

public class LeagueDaysTests
{
    [Fact]
    public void ADayStartsAtIstanbulMidnightAndClosesAtNinePmIstanbul()
    {
        var day = new DateOnly(2026, 11, 16);

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 21, 0, 0, TimeSpan.Zero), LeagueDays.Start(day));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 18, 0, 0, TimeSpan.Zero), LeagueDays.ClosesAt(day, new TimeOnly(21, 0)));
    }
}
