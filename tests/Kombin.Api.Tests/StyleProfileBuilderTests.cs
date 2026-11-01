using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Tests;

public class StyleProfileBuilderTests
{
    private static StyleCard Card(
        StyleFamily family = StyleFamily.Minimal,
        string[]? colors = null,
        short formality = 3,
        string[]? fits = null,
        Season seasons = Season.AllYear) =>
        new() { Family = family, Colors = colors ?? [], Formality = formality, Fits = fits ?? [], Seasons = seasons };

    private static StyleSwipeSample Like(StyleCard card) => new(SwipeDecision.Like, card);

    private static StyleSwipeSample Dislike(StyleCard card) => new(SwipeDecision.Dislike, card);

    private static StyleProfileData Build(IEnumerable<StyleSwipeSample> swipes, Persona? persona = null) =>
        StyleProfileBuilder.Build(swipes, persona ?? new Persona());

    [Fact]
    public void NoSwipesAndNoPreferencesGiveAnEmptyProfile()
    {
        var data = Build([]);

        Assert.Empty(data.ColorWeights);
        Assert.Empty(data.FamilyWeights);
        Assert.Empty(data.FitWeights);
        Assert.Empty(data.SeasonWeights);
        Assert.Equal(0m, data.FormalityMean);
        Assert.Equal(0, data.LikeCount);
        Assert.Equal(0, data.DislikeCount);
        Assert.Equal(0.0, data.Confidence);
    }

    [Fact]
    public void LikesAreNormalizedSoTheStrongestValueIsOne()
    {
        var data = Build(
        [
            Like(Card(colors: ["Black", "White"])),
            Like(Card(colors: ["Black"])),
            Like(Card(colors: ["Blue"])),
            Like(Card(colors: ["Black"])),
        ]);

        Assert.Equal(1.0, data.ColorWeights["Black"]);
        Assert.Equal(0.3333, data.ColorWeights["White"]);
        Assert.Equal(0.3333, data.ColorWeights["Blue"]);
    }

    [Fact]
    public void ADislikeSubtractsHalfALike()
    {
        var data = Build(
        [
            Like(Card(colors: ["Black"])),
            Like(Card(colors: ["Black"])),
            Like(Card(colors: ["White"])),
            Like(Card(colors: ["White"])),
            Dislike(Card(colors: ["Black"])),
            Dislike(Card(colors: ["Black"])),
        ]);

        // Black: 2 - 2 * 0.5 = 1, White: 2.
        Assert.Equal(0.5, data.ColorWeights["Black"]);
        Assert.Equal(1.0, data.ColorWeights["White"]);
        Assert.Equal(2, data.DislikeCount);
    }

    [Fact]
    public void WeightsNeverGoBelowZeroAndDroppedValuesAreOmitted()
    {
        var data = Build(
        [
            Like(Card(colors: ["Black"], family: StyleFamily.Classic)),
            Dislike(Card(colors: ["Blue"], family: StyleFamily.Street)),
            Dislike(Card(colors: ["Blue"], family: StyleFamily.Street)),
        ]);

        Assert.Equal(["Black"], data.ColorWeights.Keys);
        Assert.Equal(["Classic"], data.FamilyWeights.Keys);
    }

    [Fact]
    public void SkipsCountForNothing()
    {
        var data = Build([new StyleSwipeSample(SwipeDecision.Skip, Card(colors: ["Red"])), Like(Card(colors: ["Black"]))]);

        Assert.Equal(["Black"], data.ColorWeights.Keys);
        Assert.Equal(1, data.LikeCount);
        Assert.Equal(0, data.DislikeCount);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(3, 0.25)]
    [InlineData(6, 0.5)]
    [InlineData(12, 1.0)]
    [InlineData(30, 1.0)]
    public void ConfidenceGrowsWithLikesUpToTwelve(int likes, double expected)
    {
        var data = Build(Enumerable.Range(0, likes).Select(_ => Like(Card())));

        Assert.Equal(expected, data.Confidence, 6);
        Assert.Equal(likes, data.LikeCount);
    }

    [Fact]
    public void FormalityMeanIsTheMeanOfTheLikedCardsOnly()
    {
        var data = Build([Like(Card(formality: 2)), Like(Card(formality: 5)), Dislike(Card(formality: 1))]);

        Assert.Equal(3.5m, data.FormalityMean);
    }

    [Fact]
    public void FormalityMeanIsRoundedToTwoDecimals()
    {
        var data = Build([Like(Card(formality: 1)), Like(Card(formality: 1)), Like(Card(formality: 2))]);

        Assert.Equal(1.33m, data.FormalityMean);
    }

    [Fact]
    public void FamilyFitAndSeasonWeightsFollowTheLikedCards()
    {
        var data = Build(
        [
            Like(Card(StyleFamily.Street, fits: ["Oversized"], seasons: Season.Winter)),
            Like(Card(StyleFamily.Street, fits: ["Oversized", "Relaxed"], seasons: Season.Winter | Season.Autumn)),
            Like(Card(StyleFamily.Boho, fits: ["Relaxed"], seasons: Season.Summer)),
        ]);

        Assert.Equal(1.0, data.FamilyWeights["Street"]);
        Assert.Equal(0.5, data.FamilyWeights["Boho"]);
        Assert.Equal(1.0, data.FitWeights["Oversized"]);
        Assert.Equal(1.0, data.FitWeights["Relaxed"]);
        Assert.Equal(1.0, data.SeasonWeights["Winter"]);
        Assert.Equal(0.5, data.SeasonWeights["Autumn"]);
        Assert.Equal(0.5, data.SeasonWeights["Summer"]);
        Assert.DoesNotContain("Spring", data.SeasonWeights.Keys);
    }

    [Fact]
    public void AnExplicitlyPreferredColorAddsHalfALikeBeforeNormalizing()
    {
        var persona = new Persona { PreferredColors = ["Navy"] };

        var data = Build([Like(Card(colors: ["Black"]))], persona);

        Assert.Equal(1.0, data.ColorWeights["Black"]);
        Assert.Equal(0.5, data.ColorWeights["Navy"]);
    }

    [Fact]
    public void PreferencesAloneAreEnoughToHaveColorWeightsButNoConfidence()
    {
        var data = Build([], new Persona { PreferredColors = ["Navy", "Beige"], PreferredFits = ["Relaxed"] });

        Assert.Equal(1.0, data.ColorWeights["Navy"]);
        Assert.Equal(1.0, data.ColorWeights["Beige"]);
        Assert.Equal(1.0, data.FitWeights["Relaxed"]);
        Assert.Equal(0.0, data.Confidence);
    }

    [Fact]
    public void ADislikedColorIsFlooredBeforeAPreferenceIsAdded()
    {
        var persona = new Persona { PreferredColors = ["Navy"] };

        var data = Build([Dislike(Card(colors: ["Navy"])), Dislike(Card(colors: ["Navy"]))], persona);

        // Not -1 + 0.5: the dislikes bottom out at 0 first.
        Assert.Equal(1.0, data.ColorWeights["Navy"]);
    }

    [Fact]
    public void AnAvoidedColorIsRemovedAndTheRestIsRenormalized()
    {
        var persona = new Persona { AvoidedColors = ["Black"] };

        var data = Build(
        [
            Like(Card(colors: ["Black"])),
            Like(Card(colors: ["Black"])),
            Like(Card(colors: ["Black"])),
            Like(Card(colors: ["White"])),
        ], persona);

        Assert.DoesNotContain("Black", data.ColorWeights.Keys);
        Assert.Equal(1.0, data.ColorWeights["White"]);
    }

    [Fact]
    public void AvoidingWinsOverPreferring()
    {
        var persona = new Persona { PreferredColors = ["Red"], AvoidedColors = ["Red"] };

        Assert.Empty(Build([], persona).ColorWeights);
    }

    [Fact]
    public void TheSameInputAlwaysGivesTheSameProfile()
    {
        var swipes = new[] { Like(Card(colors: ["Black"])), Dislike(Card(colors: ["Red"])) };

        var first = Build(swipes);
        var second = Build(swipes);

        Assert.Equal(first.ColorWeights, second.ColorWeights);
        Assert.Equal(first.Confidence, second.Confidence);
    }
}
