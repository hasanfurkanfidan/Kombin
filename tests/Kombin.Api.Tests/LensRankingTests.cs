using Kombin.Api.Features.Lens;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Tests;

public class LensRankingTests
{
    private static LensCandidate Candidate(double similarity, decimal? price = 500m, string? url = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), url ?? $"https://img.test/{Guid.NewGuid():N}.jpg", 1 - similarity, price);

    [Theory]
    [InlineData("ust-giyim/tisort", "Top")]
    [InlineData("ust-giyim/gomlek", "Top")]
    [InlineData("ust-giyim/triko", "Top")]
    [InlineData("ust-giyim/blazer", "Outerwear")]
    [InlineData("alt-giyim/jean", "Bottom")]
    [InlineData("alt-giyim/etek", "Bottom")]
    [InlineData("dis-giyim/mont", "Outerwear")]
    [InlineData("dis-giyim/kaban", "Outerwear")]
    [InlineData("ayakkabi/sneaker", "Shoes")]
    [InlineData("ayakkabi/loafer", "Shoes")]
    [InlineData("aksesuar/canta", "Bag")]
    [InlineData("aksesuar/kemer", "Accessory")]
    [InlineData("aksesuar", "Accessory")]
    [InlineData("dis-giyim", "Outerwear")]
    public void EveryCategoryHasAFamily(string slug, string family) => Assert.Equal(Enum.Parse<CategoryFamily>(family), LensRanking.FamilyOf(slug));

    [Fact]
    public void EverySeededLeafCategoryMapsToAFamily()
    {
        var leaves = Persistence.Configurations.Catalog.CatalogSeed.Categories().Where(category => category.ParentId is not null);

        Assert.All(leaves, leaf => Assert.NotNull(LensRanking.FamilyOf(leaf.Slug)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bilinmeyen/kategori")]
    public void AnUnknownCategoryHasNoFamily(string? slug) => Assert.Null(LensRanking.FamilyOf(slug));

    [Fact]
    public void AConfidentRegionSearchesOnlyItsFamily()
    {
        var families = LensRanking.FamiliesFor("dis-giyim/mont", 0.8, ["dis-giyim/mont", "ayakkabi/bot", "ust-giyim/triko"]);

        Assert.Equal([CategoryFamily.Outerwear], families!);
    }

    [Fact]
    public void ALowConfidenceRegionIsWidenedToItsCandidatesFamiliesNotDropped()
    {
        var families = LensRanking.FamiliesFor("dis-giyim/mont", LensRanking.LowConfidence - 0.01, ["dis-giyim/mont", "ust-giyim/triko", "dis-giyim/kaban"]);

        Assert.Equal(new[] { CategoryFamily.Outerwear, CategoryFamily.Top }.Order(), families!.Order());
    }

    [Fact]
    public void ARegionWithoutACategorySearchesEverything() => Assert.Null(LensRanking.FamiliesFor(null, 0, []));

    [Theory]
    [InlineData("Women", "Women,Unisex")]
    [InlineData("Men", "Men,Unisex")]
    [InlineData("Kids", "Kids")]
    [InlineData("Unisex", "Unisex")]
    public void AnAudienceFilterLetsUnisexThroughForAGender(string audience, string expected) =>
        Assert.Equal(expected.Split(',').Select(Enum.Parse<Audience>), LensRanking.AudiencesFor(Enum.Parse<Audience>(audience)));

    [Fact]
    public void NothingBelowTheFloorIsKept()
    {
        var kept = LensRanking.Rank([Candidate(LensRanking.SimilarityFloor), Candidate(LensRanking.SimilarityFloor - 0.001)], LensRanking.SimilarityFloor);

        Assert.Single(kept);
        Assert.Empty(LensRanking.Rank([Candidate(0.3), Candidate(0.5)], LensRanking.SimilarityFloor));
    }

    [Fact]
    public void ResultsAreOrderedBySimilarityThenTheOneWithAPrice()
    {
        var best = Candidate(0.95);
        var inStock = Candidate(0.85, price: 300m);

        // 0.90 minus the sold-out penalty ties with 0.85: the product that can be bought wins the tie.
        var soldOutTie = Candidate(0.90, price: null);
        var worse = Candidate(0.80, price: 100m);

        var ranked = LensRanking.Rank([worse, soldOutTie, best, inStock], 0.7);

        Assert.Equal([best, inStock, soldOutTie, worse], ranked);
    }

    [Fact]
    public void ASoldOutProductFallsBehindItsInStockEqualsButStaysAboveClearlyWorseOnes()
    {
        var soldOut = Candidate(0.90, price: null);
        var inStockEqual = Candidate(0.88);
        var inStockWorse = Candidate(0.80);

        var ranked = LensRanking.Rank([soldOut, inStockEqual, inStockWorse], 0.7);

        Assert.Equal([inStockEqual, soldOut, inStockWorse], ranked);
        Assert.False(soldOut.Sellable);
    }

    [Fact]
    public void ProductsSharingAnImageAreOneResultAndTheInStockOneStays()
    {
        const string url = "https://IMG.test/shared.jpg";
        var soldOut = Candidate(0.9, price: null, url: url);
        var inStock = Candidate(0.9, price: 700m, url: "https://img.test/shared.jpg");

        Assert.Equal([inStock], LensRanking.Rank([soldOut, inStock], 0.7));
    }

    [Fact]
    public void TheSameImageUnderAnotherUrlIsCaughtByItsIdenticalDistance()
    {
        var original = Candidate(0.912345678, price: 400m);
        var copy = Candidate(0.912345678, price: 450m);

        Assert.Equal([original], LensRanking.Rank([copy, original], 0.7));
    }

    [Fact]
    public void ADifferentQueryStringIsADifferentImage()
    {
        var first = Candidate(0.9, url: "https://cdn.test/img?id=1");
        var second = Candidate(0.89, url: "https://cdn.test/img?id=2");

        Assert.Equal(2, LensRanking.Rank([first, second], 0.7).Count);
    }

    [Fact]
    public void AtMostTwelveResultsAreKept() =>
        Assert.Equal(LensRanking.MaxResults, LensRanking.Rank(Enumerable.Range(0, 40).Select(index => Candidate(0.99 - (index * 0.001))), 0.7).Count);

    [Fact]
    public void LevelsAreCloseOrSimilar()
    {
        Assert.Equal(LensRanking.Close, LensRanking.Level(LensRanking.CloseMatch));
        Assert.Equal(LensRanking.Similar, LensRanking.Level(LensRanking.CloseMatch - 0.001));
    }

    [Fact]
    public void WardrobePiecesAreExactSimilarOrNotShown()
    {
        Assert.Equal("exact", LensRanking.WardrobeMatch(LensRanking.WardrobeExact, cameFromAResult: false));
        Assert.Equal("exact", LensRanking.WardrobeMatch(LensRanking.WardrobeFloor, cameFromAResult: true));
        Assert.Equal("similar", LensRanking.WardrobeMatch(LensRanking.WardrobeFloor, cameFromAResult: false));
        Assert.Null(LensRanking.WardrobeMatch(LensRanking.WardrobeFloor - 0.001, cameFromAResult: true));
    }

    [Fact]
    public void TheThresholdsAreOrdered()
    {
        Assert.True(LensRanking.SimilarityFloor < LensRanking.CloseMatch);
        Assert.True(LensRanking.SimilarityFloor <= LensRanking.WholePhotoFloor);
        Assert.True(LensRanking.WardrobeFloor < LensRanking.WardrobeExact);
        Assert.True(LensRanking.Candidates >= LensRanking.MaxResults * 2);
    }
}
