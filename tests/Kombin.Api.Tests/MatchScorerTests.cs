using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Tests;

public class MatchScorerTests
{
    /// <summary>A formality 4 blazer: Classic by the family heuristic.</summary>
    private static readonly ProductTraits Blazer = new(ProductColor.Black, 4, ProductFit.Regular, Season.AllYear, ProductPattern.Solid, "ust-giyim/blazer");

    private static PersonaStyleProfile Empty(double confidence = 1.0) => new() { Confidence = confidence };

    /// <summary>Weights that make <see cref="Blazer"/> a perfect match.</summary>
    private static PersonaStyleProfile Trained(double confidence = 1.0) => new()
    {
        Confidence = confidence,
        LikeCount = 12,
        ColorWeights = new() { ["Black"] = 1.0 },
        FamilyWeights = new() { ["Classic"] = 1.0 },
        FitWeights = new() { ["Regular"] = 1.0 },
        SeasonWeights = new() { ["Spring"] = 1.0, ["Summer"] = 1.0, ["Autumn"] = 1.0, ["Winter"] = 1.0 },
        FormalityMean = 4m,
    };

    private static Persona Anyone() => new() { FormalityMin = 1, FormalityMax = 5 };

    [Fact]
    public void ATrainedPersonaScoresAPerfectProductAtLeastNinety()
    {
        Assert.Equal(100, MatchScorer.Score(Trained(), Anyone(), Blazer));
    }

    [Fact]
    public void AnUntrainedPersonaNeverScoresAPerfectProductAboveSixty()
    {
        var score = MatchScorer.Score(Trained(confidence: 0), Anyone(), Blazer);

        Assert.InRange(score, 0, 60);
        Assert.Equal(60, score);
    }

    [Fact]
    public void ConfidenceDampsLinearlyBetweenSixtyAndOneHundredPercent()
    {
        Assert.Equal(80, MatchScorer.Score(Trained(confidence: 0.5), Anyone(), Blazer));
    }

    [Fact]
    public void ColorAloneIsWorthThirtyFivePoints()
    {
        var profile = Empty();
        profile.ColorWeights = new() { ["Black"] = 1.0 };

        // The product is outside the persona's formality range so that component stays 0.
        Assert.Equal(35, MatchScorer.Score(profile, new Persona { FormalityMin = 1, FormalityMax = 2 }, Blazer));
    }

    [Fact]
    public void AColorWithoutAWeightScoresNothing()
    {
        var profile = Empty();
        profile.ColorWeights = new() { ["Navy"] = 1.0 };

        Assert.Equal(0, MatchScorer.Score(profile, new Persona { FormalityMin = 1, FormalityMax = 2 }, Blazer));
    }

    [Fact]
    public void AMultiColoredProductUsesTheAverageOfTheThreeStrongestColors()
    {
        var profile = Empty();
        profile.ColorWeights = new() { ["Black"] = 1.0, ["Navy"] = 0.5, ["Grey"] = 0.5, ["White"] = 0.25 };
        var multi = Blazer with { Color = ProductColor.Multi };

        // (1 + 0.5 + 0.5) / 3 * 35 = 23.33
        Assert.Equal(23, MatchScorer.Score(profile, new Persona { FormalityMin = 1, FormalityMax = 2 }, multi));
    }

    [Fact]
    public void AMultiColoredProductCountsMissingColorsAsZero()
    {
        var profile = Empty();
        profile.ColorWeights = new() { ["Black"] = 1.0 };
        var multi = Blazer with { Color = ProductColor.Multi };

        // 1 / 3 * 35 = 11.67
        Assert.Equal(12, MatchScorer.Score(profile, new Persona { FormalityMin = 1, FormalityMax = 2 }, multi));
    }

    [Fact]
    public void FormalityAloneIsWorthTwentyFivePointsAndFallsOffWithDistance()
    {
        var profile = Empty();
        profile.FormalityMean = 3m;

        Assert.Equal(25, MatchScorer.Score(profile, Anyone(), Blazer with { Formality = 3 }));

        // 1 - |2 - 3| / 4 = 0.75
        Assert.Equal(19, MatchScorer.Score(profile, Anyone(), Blazer with { Formality = 2 }));
    }

    [Fact]
    public void AProductOutsideThePersonasFormalityRangeGetsNoFormalityPoints()
    {
        var profile = Empty();
        profile.FormalityMean = 4m;

        Assert.Equal(25, MatchScorer.Score(profile, new Persona { FormalityMin = 3, FormalityMax = 5 }, Blazer));
        Assert.Equal(0, MatchScorer.Score(profile, new Persona { FormalityMin = 1, FormalityMax = 3 }, Blazer));
    }

    [Fact]
    public void APersonaThatLikedNothingYetUsesTheMiddleOfItsRangeAsItsFormality()
    {
        var persona = new Persona { FormalityMin = 3, FormalityMax = 5 };

        // Middle is 4: a formality 4 product is a perfect fit, a formality 3 one is 1 / 4 off.
        Assert.Equal(25, MatchScorer.Score(Empty(), persona, Blazer));
        Assert.Equal(19, MatchScorer.Score(Empty(), persona, Blazer with { Formality = 3 }));
    }

    [Fact]
    public void FitAloneIsWorthFifteenPointsAndAnUnknownFitIsNeutral()
    {
        var profile = Empty();
        profile.FitWeights = new() { ["Regular"] = 1.0 };
        var outOfRange = new Persona { FormalityMin = 1, FormalityMax = 2 };

        Assert.Equal(15, MatchScorer.Score(profile, outOfRange, Blazer));
        Assert.Equal(0, MatchScorer.Score(profile, outOfRange, Blazer with { Fit = ProductFit.Slim }));
        Assert.InRange(MatchScorer.Score(Empty(), outOfRange, Blazer with { Fit = ProductFit.Unknown }), 7, 8);
    }

    [Fact]
    public void SeasonAloneIsWorthFifteenPointsForTheSeasonsTheProductIsSoldIn()
    {
        var profile = Empty();
        profile.SeasonWeights = new() { ["Winter"] = 1.0 };
        var outOfRange = new Persona { FormalityMin = 1, FormalityMax = 2 };

        Assert.Equal(15, MatchScorer.Score(profile, outOfRange, Blazer with { Seasons = Season.Winter }));
        Assert.Equal(0, MatchScorer.Score(profile, outOfRange, Blazer with { Seasons = Season.Summer }));
    }

    [Fact]
    public void SeasonWeightsAddUpButAreCappedAtOne()
    {
        var profile = Empty();
        profile.SeasonWeights = new() { ["Spring"] = 0.6, ["Summer"] = 0.6 };
        var outOfRange = new Persona { FormalityMin = 1, FormalityMax = 2 };

        Assert.Equal(15, MatchScorer.Score(profile, outOfRange, Blazer with { Seasons = Season.Spring | Season.Summer }));
        Assert.Equal(9, MatchScorer.Score(profile, outOfRange, Blazer with { Seasons = Season.Spring }));
    }

    [Fact]
    public void FamilyAloneIsWorthTenPoints()
    {
        var profile = Empty();
        profile.FamilyWeights = new() { ["Classic"] = 1.0 };
        var outOfRange = new Persona { FormalityMin = 1, FormalityMax = 3 };

        Assert.Equal(10, MatchScorer.Score(profile, outOfRange, Blazer));
        Assert.Equal(0, MatchScorer.Score(profile, outOfRange, Blazer with { Formality = 5 }));
    }

    [Fact]
    public void AnAvoidedColorCapsTheScoreAtTwentyFive()
    {
        var persona = new Persona { FormalityMin = 1, FormalityMax = 5, AvoidedColors = ["Black"] };

        Assert.Equal(25, MatchScorer.Score(Trained(), persona, Blazer));

        // Any other color is not capped (the color component is simply 0 here).
        Assert.Equal(65, MatchScorer.Score(Trained(), persona, Blazer with { Color = ProductColor.Navy }));
    }

    [Fact]
    public void AnAvoidedColorNeverRaisesALowScore()
    {
        var persona = new Persona { FormalityMin = 1, FormalityMax = 2, AvoidedColors = ["Black"] };

        Assert.Equal(0, MatchScorer.Score(Empty(), persona, Blazer with { Fit = ProductFit.Slim }));
    }

    [Fact]
    public void ScoresStayWithinZeroAndOneHundred()
    {
        var wild = Trained(confidence: 5);
        wild.ColorWeights = new() { ["Black"] = 40.0 };

        Assert.InRange(MatchScorer.Score(wild, Anyone(), Blazer), 0, 100);
    }

    [Fact]
    public void TheProductOverloadReadsItsTraitsFromTheEntity()
    {
        var product = new Product
        {
            PrimaryColor = ProductColor.Black,
            Formality = 4,
            Fit = ProductFit.Regular,
            Seasons = Season.AllYear,
            Pattern = ProductPattern.Solid,
            Category = new Category { Slug = "ust-giyim/blazer" },
        };

        Assert.Equal(100, MatchScorer.Score(Trained(), Anyone(), product));
    }

    [Theory]
    [InlineData("ust-giyim/blazer", 5, "Solid", "Formal")]
    [InlineData("ayakkabi/loafer", 5, "Solid", "Formal")]
    [InlineData("ust-giyim/blazer", 4, "Solid", "Classic")]
    [InlineData("ust-giyim/gomlek", 4, "Striped", "Classic")]
    [InlineData("ayakkabi/sneaker", 1, "Solid", "Sporty")]
    [InlineData("ayakkabi/sneaker", 2, "Solid", "Sporty")]
    [InlineData("ayakkabi/sneaker", 3, "Solid", "Minimal")]
    [InlineData("ust-giyim/tisort", 2, "Printed", "Street")]
    [InlineData("alt-giyim/jean", 2, "Solid", "Street")]
    [InlineData("ust-giyim/tisort", 1, "Solid", "Sporty")]
    [InlineData("ayakkabi/bot", 3, "Solid", "Edgy")]
    [InlineData("alt-giyim/etek", 3, "Solid", "Romantic")]
    [InlineData("ust-giyim/tisort", 3, "Printed", "Boho")]
    [InlineData("ust-giyim/triko", 3, "Checked", "Classic")]
    [InlineData("ust-giyim/triko", 3, "Solid", "Minimal")]
    [InlineData("aksesuar", 2, "Solid", "Minimal")]
    public void AProductFamilyIsGuessedFromCategoryFormalityAndPattern(string category, int formality, string pattern, string expected)
    {
        Assert.Equal(Enum.Parse<StyleFamily>(expected), ProductFamilies.Derive(category, formality, Enum.Parse<ProductPattern>(pattern)));
    }
}
