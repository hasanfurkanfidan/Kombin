using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Tests;

public class WardrobeSummaryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string> RootNames = new() { ["ust-giyim"] = "Üst giyim", ["ayakkabi"] = "Ayakkabı" };

    private static SummaryPiece Piece(string? root, decimal? own, decimal? catalog = null, int wears = 0, DateOnly? last = null)
    {
        var (price, kind) = WardrobeSummaryRules.Resolve(own, catalog);
        return new SummaryPiece(Guid.CreateVersion7(), root, price, kind, wears, last);
    }

    [Fact]
    public void AnOwnPriceBeatsTheCatalogPriceAndIsAFact()
    {
        Assert.Equal((500m, PriceKind.Own), WardrobeSummaryRules.Resolve(500m, 300m));
    }

    [Fact]
    public void ALinkedProductsPriceIsAnEstimateAndNothingIsNone()
    {
        Assert.Equal((300m, PriceKind.Estimated), WardrobeSummaryRules.Resolve(null, 300m));
        Assert.Equal((null, PriceKind.None), WardrobeSummaryRules.Resolve(null, null));
    }

    [Fact]
    public void AFreeGiftIsAPriceOfZeroNotAMissingOne()
    {
        Assert.Equal((0m, PriceKind.Own), WardrobeSummaryRules.Resolve(0m, 300m));
    }

    [Fact]
    public void TheValueSeparatesFactEstimateAndUnpricedAndTheCountsAddUp()
    {
        var pieces = new List<SummaryPiece>
        {
            Piece("ust-giyim", 1000m),
            Piece("ust-giyim", 500m),
            Piece("ust-giyim", null, 250m),
            Piece("ayakkabi", null, 750m),
            Piece("ayakkabi", null),
            Piece(null, null),
        };

        var value = WardrobeSummaryRules.Value(pieces, RootNames);

        Assert.Equal(2500m, value.Total);
        Assert.Equal(1500m, value.KnownTotal);
        Assert.Equal(1000m, value.EstimatedTotal);
        Assert.True(value.IsEstimate);
        Assert.Equal(new ValueCounts(2, 2, 2), value.Counts);
        Assert.Equal(pieces.Count, value.Counts.Priced + value.Counts.Estimated + value.Counts.Unpriced);
        Assert.Null(value.Note);
        Assert.Equal("TRY", value.Currency);
    }

    [Fact]
    public void TheCategoriesAreTheTopLevelOnesWithTheirOwnEstimateFlag()
    {
        var value = WardrobeSummaryRules.Value(
            [Piece("ust-giyim", 1000m), Piece("ust-giyim", null), Piece("ayakkabi", null, 750m), Piece(null, null)],
            RootNames);

        var tops = value.ByCategory.Single(category => category.Slug == "ust-giyim");
        Assert.Equal("Üst giyim", tops.Name);
        Assert.Equal((2, 1, 1000m, 0m, false), (tops.PieceCount, tops.UnpricedCount, tops.Total, tops.EstimatedTotal, tops.IsEstimate));

        var shoes = value.ByCategory.Single(category => category.Slug == "ayakkabi");
        Assert.Equal((750m, 750m, true), (shoes.Total, shoes.EstimatedTotal, shoes.IsEstimate));

        var none = value.ByCategory.Single(category => category.Slug is null);
        Assert.Equal(WardrobeSummaryCopy.UncategorisedName, none.Name);
        Assert.Null(none.Total);

        // The biggest first, a category with nothing priced last.
        Assert.Equal(["ust-giyim", "ayakkabi", null], value.ByCategory.Select(category => category.Slug));
    }

    [Fact]
    public void WithNothingPricedThereIsNoTotalNotAZero()
    {
        var value = WardrobeSummaryRules.Value([Piece("ust-giyim", null), Piece("ust-giyim", null)], RootNames);

        Assert.Null(value.Total);
        Assert.False(value.IsEstimate);
        Assert.Equal(WardrobeSummaryCopy.NothingPriced, value.Note);
        Assert.Equal(new ValueCounts(0, 0, 2), value.Counts);
    }

    [Fact]
    public void ACostPerWearNeedsAPriceAndAWearAndIsPricePerWear()
    {
        var worn = Piece("ust-giyim", 900m, wears: 4, last: new DateOnly(2026, 11, 1));
        var estimated = Piece("ust-giyim", null, 100m, wears: 3, last: new DateOnly(2026, 11, 2));
        var unworn = Piece("ust-giyim", 500m, wears: 0);
        var unpriced = Piece("ust-giyim", null, wears: 9, last: new DateOnly(2026, 11, 3));

        var entries = WardrobeSummaryRules.CostPerWear([unworn, unpriced, estimated, worn]);

        Assert.Equal([worn.Id, estimated.Id], entries.Select(entry => entry.Piece.Id));
        Assert.Equal([225m, 33.33m], entries.Select(entry => entry.CostPerWear));
        Assert.Equal(PriceKind.Estimated, entries[1].Piece.PriceKind);
    }

    [Fact]
    public void TheCostPerWearListIsTheMostWornFiveAndTiesGoToTheLatestWorn()
    {
        var pieces = Enumerable.Range(1, 7)
            .Select(index => Piece("ust-giyim", 100m * index, wears: index == 7 ? 1 : 10 - index, last: new DateOnly(2026, 11, index)))
            .ToList();
        var tieOlder = Piece("ust-giyim", 10m, wears: 9, last: new DateOnly(2026, 1, 1));
        var tieNewer = Piece("ust-giyim", 10m, wears: 9, last: new DateOnly(2026, 6, 1));

        var entries = WardrobeSummaryRules.CostPerWear([.. pieces, tieOlder, tieNewer]);

        Assert.Equal(WardrobeSummaryRules.CostPerWearCount, entries.Count);
        Assert.Equal([pieces[0].Id, tieNewer.Id, tieOlder.Id], entries.Take(3).Select(entry => entry.Piece.Id));
        Assert.Equal(entries.Select(entry => entry.Piece.WearCount).OrderByDescending(wears => wears), entries.Select(entry => entry.Piece.WearCount));
    }

    [Fact]
    public void APieceIsUnwornAfterExactlyThirtyDaysWithNoWearAndNotBefore()
    {
        Assert.True(WardrobeSummaryRules.IsUnwornCandidate(Now.AddDays(-30), 0, null, Now));
        Assert.True(WardrobeSummaryRules.IsUnwornCandidate(Now.AddDays(-200), 0, null, Now));
        Assert.False(WardrobeSummaryRules.IsUnwornCandidate(Now.AddDays(-30).AddSeconds(1), 0, null, Now));
        Assert.False(WardrobeSummaryRules.IsUnwornCandidate(Now.AddDays(-200), 1, new DateOnly(2026, 1, 1), Now));
        Assert.False(WardrobeSummaryRules.IsUnwornCandidate(Now.AddDays(-200), 0, new DateOnly(2026, 1, 1), Now));
    }

    [Theory]
    [InlineData(true, "Winter", "Summer", "NoPartner")]
    [InlineData(true, "AllYear", "Summer", "NoPartner")]
    [InlineData(false, "Winter", "Summer", "OutOfSeason")]
    [InlineData(false, "Spring, Summer", "Autumn", "OutOfSeason")]
    public void TheReasonIsNoPartnerFirstThenOutOfSeason(bool noPartner, string pieceSeasons, string now, string expected)
    {
        Assert.Equal(Enum.Parse<UnwornReason>(expected), WardrobeSummaryRules.ReasonOf(noPartner, Enum.Parse<Season>(pieceSeasons), Enum.Parse<Season>(now)));
    }

    [Theory]
    [InlineData("AllYear", "Summer")]
    [InlineData("Summer", "Summer")]
    [InlineData("None", "Summer")]
    public void ThereIsNoReasonInSeasonOrWithoutASeason(string pieceSeasons, string now)
    {
        Assert.Null(WardrobeSummaryRules.ReasonOf(false, Enum.Parse<Season>(pieceSeasons), Enum.Parse<Season>(now)));
    }

    [Fact]
    public void TheReasonsAreSaidTheGlossaryWay()
    {
        Assert.Equal("eşi yok", WardrobeSummaryRules.ReasonText(UnwornReason.NoPartner));
        Assert.Equal("mevsimi değil", WardrobeSummaryRules.ReasonText(UnwornReason.OutOfSeason));
    }
}
