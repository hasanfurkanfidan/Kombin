using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Tests;

public class GapAnalysisTests
{
    private static GapPiece Top(ProductColor color, int formality = 2) => new(SizeGroup.Tops, color, formality);

    private static GapPiece Bottom(ProductColor color, int formality = 2) => new(SizeGroup.Bottoms, color, formality);

    private static GapPiece Shoes(ProductColor color, int formality = 2) => new(SizeGroup.Shoes, color, formality);

    private static GapPiece Coat(ProductColor color, int formality = 2) => new(SizeGroup.Outerwear, color, formality);

    private static GapPersona Persona(
        int min = 1, int max = 2, ProductColor[]? preferred = null, ProductColor[]? avoided = null, Season seasons = Season.AllYear) =>
        new(min, max, preferred ?? [], avoided ?? [], seasons);

    [Fact]
    public void AnEmptyWardrobeWithoutAPersonaListsTheBasicsInOrder()
    {
        var gaps = GapAnalysis.Analyze([], persona: null);

        Assert.Equal(
            ["missing_casual_top", "missing_casual_bottom", "missing_casual_shoes", "missing_smart_top", "missing_smart_bottom"],
            gaps.Select(gap => gap.Kind));
        Assert.All(gaps, gap => Assert.Equal(0, gap.UnlocksEstimate));
        Assert.All(gaps, gap => Assert.Contains(gap.Color, GapAnalysis.Neutrals));

        var first = gaps[0];
        Assert.Equal("Dolabında hiç günlük üst yok", first.Message);
        Assert.Equal("ust-giyim", first.CategorySlug);
        Assert.Equal(ProductColor.White, first.Color);
        Assert.Equal(2, first.Formality);
    }

    [Fact]
    public void ABalancedWardrobeHasNoGapsInItsBand()
    {
        var wardrobe = new[] { Top(ProductColor.White), Top(ProductColor.Red), Bottom(ProductColor.Navy), Shoes(ProductColor.Black), Coat(ProductColor.Beige) };

        Assert.Empty(GapAnalysis.Analyze(wardrobe, Persona(min: 1, max: 2)));
    }

    [Fact]
    public void OnlyColorfulTopsMakeANeutralTopTheGapAndItsUnlocksAreBottomsTimesShoes()
    {
        var wardrobe = new[]
        {
            Top(ProductColor.Red), Top(ProductColor.Green),
            Bottom(ProductColor.Navy), Bottom(ProductColor.Black), Bottom(ProductColor.Blue),
            Shoes(ProductColor.White), Shoes(ProductColor.Black),
            Coat(ProductColor.Black),
        };

        var gap = Assert.Single(GapAnalysis.Analyze(wardrobe, Persona(min: 1, max: 2)));

        Assert.Equal("missing_neutral_top", gap.Kind);
        Assert.Equal(ProductColor.White, gap.Color);
        Assert.Equal("Dolabında beyaz tonlarda üst yok", gap.Message);
        Assert.Equal(3 * 2, gap.UnlocksEstimate);
        Assert.Equal(FormalityBand.Casual, gap.Band);
    }

    [Fact]
    public void TheSuggestedColorIsThePersonasFirstPreferredNeutralItDoesNotAvoid()
    {
        var wardrobe = new[] { Top(ProductColor.Red), Bottom(ProductColor.Navy), Shoes(ProductColor.Black), Coat(ProductColor.Black) };

        var preferred = Assert.Single(GapAnalysis.Analyze(wardrobe, Persona(preferred: [ProductColor.Red, ProductColor.Beige, ProductColor.Grey])));
        Assert.Equal(ProductColor.Beige, preferred.Color);
        Assert.Equal("Dolabında bej tonlarda üst yok", preferred.Message);

        // No preference: the group's default order without the avoided colors.
        var avoided = Assert.Single(GapAnalysis.Analyze(wardrobe, Persona(avoided: [ProductColor.White])));
        Assert.Equal(ProductColor.Beige, avoided.Color);
    }

    [Fact]
    public void ColorsLearnedFromSwipesFollowTheExplicitPreferencesStrongestFirst()
    {
        var persona = new Persona { FormalityMin = 1, FormalityMax = 2, PreferredColors = ["Red"], AvoidedColors = ["White"] };
        var profile = new PersonaStyleProfile { ColorWeights = new() { ["Grey"] = 0.2, ["Black"] = 0.6, ["Navy"] = 0, ["Red"] = 0.4 } };

        var gapPersona = GapPersona.From(persona, profile);
        Assert.Equal([ProductColor.Red, ProductColor.Black, ProductColor.Grey], gapPersona.PreferredColors);

        var gap = Assert.Single(GapAnalysis.Analyze([Top(ProductColor.Red), Bottom(ProductColor.Navy), Shoes(ProductColor.Black), Coat(ProductColor.Black)], gapPersona));
        Assert.Equal(ProductColor.Black, gap.Color);
        Assert.Equal("Dolabında siyah tonlarda üst yok", gap.Message);

        // Without a learned profile only the explicit preferences count: no neutral among them, so the default order (minus White).
        Assert.Equal(ProductColor.Beige, Assert.Single(GapAnalysis.Analyze([Top(ProductColor.Red), Bottom(ProductColor.Navy), Shoes(ProductColor.Black), Coat(ProductColor.Black)], GapPersona.From(persona, null))).Color);
    }

    [Fact]
    public void AOneCategoryWardrobeRanksTheGapsThatUnlockTheMostFirst()
    {
        // Only tops: a bottom, shoes or a coat alone unlock nothing yet (another group is empty), so the order falls back to the rules.
        var tops = new[] { Top(ProductColor.White), Top(ProductColor.Black), Top(ProductColor.Grey) };

        var gaps = GapAnalysis.Analyze(tops, Persona(min: 1, max: 2));
        Assert.Equal(["missing_casual_bottom", "missing_casual_shoes", "missing_outerwear"], gaps.Select(gap => gap.Kind));
        Assert.All(gaps, gap => Assert.Equal(0, gap.UnlocksEstimate));

        // With two pairs of shoes the missing bottom completes tops × shoes outfits and leads.
        var withShoes = tops.Append(Shoes(ProductColor.Red)).Append(Shoes(ProductColor.Green)).ToArray();
        var ranked = GapAnalysis.Analyze(withShoes, Persona(min: 1, max: 2));

        Assert.Equal("missing_casual_bottom", ranked[0].Kind);
        Assert.Equal(3 * 2, ranked[0].UnlocksEstimate);
        Assert.Equal("missing_neutral_shoes", ranked[1].Kind);
    }

    [Fact]
    public void AFormalPersonaIsJudgedOnFormalPiecesOnly()
    {
        var casual = new[] { Top(ProductColor.White, 2), Bottom(ProductColor.Navy, 2), Shoes(ProductColor.White, 1) };

        var gaps = GapAnalysis.Analyze(casual, Persona(min: 4, max: 5, seasons: Season.Summer));

        Assert.Equal(["missing_formal_top", "missing_formal_bottom", "missing_formal_shoes"], gaps.Select(gap => gap.Kind));
        Assert.Equal("Dolabında hiç resmi ayakkabı yok", gaps[2].Message);
        Assert.All(gaps, gap => Assert.Equal(4, gap.Formality));
    }

    [Fact]
    public void UnlocksCountOnlyPiecesWithinOneFormalityStep()
    {
        // A smart top (3) would go with the 2- and 4-formality bottoms and shoes, not the sporty 1s.
        var wardrobe = new[]
        {
            Bottom(ProductColor.Navy, 2), Bottom(ProductColor.Black, 4), Bottom(ProductColor.Grey, 1),
            Shoes(ProductColor.Brown, 4), Shoes(ProductColor.White, 1),
            Top(ProductColor.Red, 3), Bottom(ProductColor.Beige, 3), Shoes(ProductColor.Black, 3), Coat(ProductColor.Navy, 3),
        };

        var gap = Assert.Single(GapAnalysis.Analyze(wardrobe, Persona(min: 3, max: 3)));

        Assert.Equal("missing_neutral_top", gap.Kind);
        Assert.Equal(3 * 2, gap.UnlocksEstimate);
    }

    [Fact]
    public void OuterwearIsOnlyAGapForAPersonaThatDressesForTheColdSeasons()
    {
        var wardrobe = new[] { Top(ProductColor.White), Bottom(ProductColor.Navy), Shoes(ProductColor.Black) };

        var winter = Assert.Single(GapAnalysis.Analyze(wardrobe, Persona(seasons: Season.Autumn | Season.Winter)));
        Assert.Equal("missing_outerwear", winter.Kind);
        Assert.Equal("dis-giyim", winter.CategorySlug);
        Assert.Equal(1, winter.UnlocksEstimate);
        Assert.Equal("Dolabında soğuk günler için dış giyim yok", winter.Message);

        Assert.Empty(GapAnalysis.Analyze(wardrobe, Persona(seasons: Season.Spring | Season.Summer)));
    }

    [Fact]
    public void APersonaSpanningSeveralBandsNamesTheBandInNeutralGaps()
    {
        var wardrobe = new[]
        {
            Top(ProductColor.Red, 2), Bottom(ProductColor.Navy, 2), Shoes(ProductColor.Black, 2),
            Top(ProductColor.Green, 3), Bottom(ProductColor.Navy, 3), Shoes(ProductColor.Black, 3),
            Coat(ProductColor.Black),
        };

        var gaps = GapAnalysis.Analyze(wardrobe, Persona(min: 2, max: 3));

        Assert.Equal(["Dolabında beyaz tonlarda günlük üst yok", "Dolabında beyaz tonlarda smart casual üst yok"], gaps.Select(gap => gap.Message));
    }

    [Fact]
    public void AtMostFiveGapsAreReported()
    {
        var gaps = GapAnalysis.Analyze([], Persona(min: 1, max: 5, seasons: Season.Winter));

        Assert.Equal(GapAnalysis.MaxGaps, gaps.Count);
    }
}
