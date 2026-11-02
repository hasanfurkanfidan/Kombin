using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using static Kombin.Api.Tests.OutfitPieces;

namespace Kombin.Api.Tests;

/// <summary>One test (or theory) per rule table: what a stylist would check when arguing with the file.</summary>
public class OutfitRulesTests
{
    // ---- colour ----

    [Fact]
    public void AllNeutralsAreSafeButNotTheBestScore()
    {
        var verdict = ColorRules.Judge([Tee(ProductColor.White), Trousers(ProductColor.Beige), Sneaker(ProductColor.White)]);

        Assert.True(verdict.Allowed);
        Assert.Equal(ColorHarmony.AllNeutral, verdict.Harmony);
        Assert.Equal(ColorRules.AllNeutralScore, verdict.Score);
    }

    [Theory]
    [InlineData("Black", "Navy")]
    [InlineData("Black", "Brown")]
    public void QuietNeutralPairsAreAllowedButScoreLower(string firstName, string secondName)
    {
        var first = Enum.Parse<ProductColor>(firstName);
        var second = Enum.Parse<ProductColor>(secondName);

        var verdict = ColorRules.Judge([Tee(first), Trousers(second), Sneaker(ProductColor.White)]);

        Assert.True(verdict.Allowed);
        Assert.Equal(ColorHarmony.QuietNeutrals, verdict.Harmony);
        Assert.True(verdict.Score < ColorRules.AllNeutralScore);
    }

    [Fact]
    public void OneStatementColourOnANeutralBaseIsTheBestOutfit()
    {
        var verdict = ColorRules.Judge([Tee(ProductColor.Red), Trousers(ProductColor.Navy), Sneaker(ProductColor.White)]);

        Assert.Equal(ColorHarmony.OneStatement, verdict.Harmony);
        Assert.Equal(ProductColor.Red, verdict.Statement);
        Assert.Equal(ColorRules.OneStatementScore, verdict.Score);
    }

    [Fact]
    public void BlueJeansAreANeutralButABlueShirtIsAColour()
    {
        Assert.Equal(ColorHarmony.OneStatement, ColorRules.Judge([Tee(ProductColor.Red), Jeans(ProductColor.Blue), Sneaker(ProductColor.White)]).Harmony);
        Assert.Equal(ColorHarmony.Monochrome, ColorRules.Judge([Shirt(ProductColor.Blue), Trousers(ProductColor.Blue), Sneaker(ProductColor.White)]).Harmony);
    }

    [Theory]
    [InlineData("Red", "Orange", "Analogous")]
    [InlineData("Green", "Blue", "Analogous")]
    [InlineData("Pink", "Purple", "Analogous")]
    [InlineData("Blue", "Orange", "Complementary")]
    [InlineData("Red", "Green", "Complementary")]
    [InlineData("Yellow", "Purple", "Complementary")]
    public void TwoColoursWorkOnlyAsAListedPair(string topName, string bottomName, string expectedName)
    {
        var top = Enum.Parse<ProductColor>(topName);
        var bottom = Enum.Parse<ProductColor>(bottomName);
        var expected = Enum.Parse<ColorHarmony>(expectedName);

        var verdict = ColorRules.Judge([Tee(top), Trousers(bottom), Sneaker(ProductColor.White)]);
        var turned = ColorRules.Judge([Tee(bottom), Trousers(top), Sneaker(ProductColor.White)]);

        Assert.True(verdict.Allowed);
        Assert.Equal(expected, verdict.Harmony);
        Assert.Equal(verdict, turned);
    }

    [Theory]
    [InlineData("Red", "Purple")]
    [InlineData("Yellow", "Pink")]
    [InlineData("Orange", "Green")]
    [InlineData("Multi", "Red")]
    public void UnlistedColourPairsAndPrintsWithAColourAreRejected(string topName, string bottomName)
    {
        var top = Enum.Parse<ProductColor>(topName);
        var bottom = Enum.Parse<ProductColor>(bottomName);

        Assert.False(ColorRules.Judge([Tee(top), Trousers(bottom), Sneaker(ProductColor.White)]).Allowed);
    }

    [Fact]
    public void ThreeColoursAreRejected()
    {
        Assert.False(ColorRules.Judge([Tee(ProductColor.Red), Trousers(ProductColor.Orange), Sneaker(ProductColor.Yellow)]).Allowed);
    }

    [Fact]
    public void PartnersOfAColourAreTheNeutralsItselfAndItsPairs()
    {
        var partners = ColorRules.PartnersOf(ProductColor.Blue);

        Assert.Superset(new HashSet<ProductColor> { ProductColor.Black, ProductColor.White, ProductColor.Blue, ProductColor.Green, ProductColor.Purple, ProductColor.Orange }, partners.ToHashSet());
        Assert.DoesNotContain(ProductColor.Red, partners);
        Assert.Equal(6, ColorRules.PartnersOf(ProductColor.Beige).Count);
    }

    // ---- formality ----

    [Theory]
    [InlineData(new[] { 3, 3, 3 }, null, true, FormalityRules.SameLevelScore)]
    [InlineData(new[] { 2, 3, 3 }, null, true, FormalityRules.OneStepScore)]
    [InlineData(new[] { 2, 4, 3 }, null, false, 0.0)]
    [InlineData(new[] { 2, 2, 2 }, 3, true, FormalityRules.SameLevelScore)]
    [InlineData(new[] { 2, 2, 2 }, 4, false, 0.0)]
    [InlineData(new[] { 4, 4, 4 }, 3, true, FormalityRules.SameLevelScore)]
    [InlineData(new[] { 4, 4, 4 }, 2, false, 0.0)]
    public void CorePiecesSpreadAtMostOneAndTheLayerReachesOneFurther(int[] core, int? outerwear, bool allowed, double score)
    {
        var verdict = FormalityRules.Judge(core, outerwear);

        Assert.Equal(allowed, verdict.Allowed);
        Assert.Equal(score, verdict.Score);
    }

    // ---- silhouette ----

    [Theory]
    [InlineData("Oversized", "Slim", true, 1.0)]
    [InlineData("Oversized", "Regular", true, 0.95)]
    [InlineData("Oversized", "Relaxed", true, 0.6)]
    [InlineData("Oversized", "Oversized", false, 0.0)]
    [InlineData("Slim", "Relaxed", true, 0.95)]
    [InlineData("Relaxed", "Relaxed", true, 0.75)]
    [InlineData("Unknown", "Oversized", true, SilhouetteRules.UnknownScore)]
    public void VolumeOnOneHalfWantsACloserLineOnTheOther(string topName, string bottomName, bool allowed, double score)
    {
        var top = Enum.Parse<ProductFit>(topName);
        var bottom = Enum.Parse<ProductFit>(bottomName);

        var verdict = SilhouetteRules.Judge(top, bottom);

        Assert.Equal(allowed, verdict.Allowed);
        Assert.Equal(score, verdict.Score);
    }

    [Fact]
    public void EveryFitPairHasAnEntry()
    {
        var known = Enum.GetValues<ProductFit>().Where(fit => fit != ProductFit.Unknown).ToList();

        foreach (var top in known)
        {
            foreach (var bottom in known)
            {
                Assert.InRange(SilhouetteRules.Judge(top, bottom).Score, 0, 1);
            }
        }
    }

    // ---- pattern ----

    [Fact]
    public void OneStrongPatternIsAStatementTwoBigOnesClash()
    {
        Assert.Equal((true, PatternRules.OnePatternScore), PatternRules.Judge([Tee(ProductColor.White, pattern: ProductPattern.Striped), Trousers(ProductColor.Navy)]));
        Assert.False(PatternRules.Judge([Tee(ProductColor.White, pattern: ProductPattern.Striped), Trousers(ProductColor.Navy, pattern: ProductPattern.Checked)]).Allowed);
        Assert.False(PatternRules.Judge([Tee(ProductColor.White, pattern: ProductPattern.Printed), Trousers(ProductColor.Navy, pattern: ProductPattern.Printed)]).Allowed);
    }

    [Fact]
    public void TwoSmallScalePatternsCanSitTogether()
    {
        var pinstripe = Shirt(ProductColor.White, pattern: ProductPattern.Striped);
        var checkedBelt = Piece("aksesuar/kemer", ProductColor.Brown, 3, ProductPattern.Checked);

        Assert.Equal((true, PatternRules.SmallPatternsScore), PatternRules.Judge([pinstripe, Trousers(ProductColor.Navy), checkedBelt]));
    }

    [Fact]
    public void TextureIsNotAPatternAndThreePatternsAreAlwaysTooMany()
    {
        Assert.Equal((true, PatternRules.NoPatternScore), PatternRules.Judge([Tee(ProductColor.Grey, pattern: ProductPattern.Textured), Trousers(ProductColor.Navy, pattern: ProductPattern.Textured)]));

        var gingham = Shirt(ProductColor.White, pattern: ProductPattern.Checked);
        var pinstripe = Shirt(ProductColor.Blue, pattern: ProductPattern.Striped, name: "pinstripe");
        var belt = Piece("aksesuar/kemer", ProductColor.Brown, 3, ProductPattern.Striped);
        Assert.False(PatternRules.Judge([gingham, pinstripe, belt]).Allowed);
    }

    // ---- weather ----

    [Theory]
    [InlineData(30, 22, "Hot")]
    [InlineData(24, 16, "Warm")]
    [InlineData(18, 10, "Mild")]
    [InlineData(12, 4, "Cool")]
    [InlineData(5, -2, "Cold")]
    public void TheDressingTemperaturePicksTheBand(double max, double min, string bandName)
    {
        var band = Enum.Parse<TemperatureBand>(bandName);

        Assert.Equal(band, WeatherRules.From(Forecast(min, max)).Band);
    }

    [Fact]
    public void HotDaysForbidLayersAndBanKnitwearAndBoots()
    {
        var hot = WeatherRules.From(Forecast(24, 33));

        Assert.Equal(OuterwearPolicy.Forbidden, hot.Outerwear);
        Assert.Null(WeatherRules.PieceFit(Piece("ust-giyim/triko", ProductColor.Grey), hot));
        Assert.Null(WeatherRules.PieceFit(Boot(ProductColor.Brown), hot));
        Assert.NotNull(WeatherRules.PieceFit(Sneaker(ProductColor.White), hot));
    }

    [Fact]
    public void ColdDaysRequireARealCoat()
    {
        var cold = WeatherRules.From(Forecast(-3, 3));

        Assert.Equal(OuterwearPolicy.Required, cold.Outerwear);
        Assert.NotNull(WeatherRules.PieceFit(Coat(ProductColor.Black), cold));
        Assert.Null(WeatherRules.PieceFit(Blazer(ProductColor.Black), cold));
        Assert.Null(WeatherRules.PieceFit(Jacket(ProductColor.Black), cold));
    }

    [Fact]
    public void RainRequiresALayerAndPrefersBootsOverWhiteSneakersAndLoafers()
    {
        var rain = WeatherRules.From(Forecast(14, 19) with { PrecipitationProbability = 80 });

        Assert.True(rain.Rain);
        Assert.Equal(OuterwearPolicy.Required, rain.Outerwear);

        var boots = WeatherRules.PieceFit(Boot(ProductColor.Brown), rain)!.Value;
        var sneakers = WeatherRules.PieceFit(Sneaker(ProductColor.Black), rain)!.Value;
        var whiteSneakers = WeatherRules.PieceFit(Sneaker(ProductColor.White), rain)!.Value;
        var loafers = WeatherRules.PieceFit(Loafer(ProductColor.Brown), rain)!.Value;

        Assert.True(boots > sneakers);
        Assert.True(sneakers > whiteSneakers);
        Assert.True(sneakers > loafers);
        Assert.Null(WeatherRules.PieceFit(Piece("ayakkabi/sandalet", ProductColor.Brown), rain));
    }

    [Theory]
    [InlineData(null, 2.0, null, true)]
    [InlineData(null, null, 61, true)]
    [InlineData(20, 0.2, 3, false)]
    public void RainComesFromProbabilityAmountOrWeatherCode(int? probability, double? millimetres, int? code, bool rainy)
    {
        Assert.Equal(rainy, WeatherRules.IsRainy(Forecast(10, 15) with { PrecipitationProbability = probability, PrecipitationMm = millimetres, WeatherCode = code }));
    }

    [Theory]
    [InlineData(1, "Winter", "Required")]
    [InlineData(4, "Spring", "Optional")]
    [InlineData(7, "Summer", "Forbidden")]
    [InlineData(10, "Autumn", "Optional")]
    public void WithoutAForecastTheSeasonDecides(int month, string seasonName, string outerwearName)
    {
        var season = Enum.Parse<Season>(seasonName);
        var outerwear = Enum.Parse<OuterwearPolicy>(outerwearName);

        var conditions = WeatherRules.FromSeason(new DateOnly(2026, month, 10));

        Assert.Equal(season, conditions.Seasons);
        Assert.Equal(outerwear, conditions.Outerwear);
        Assert.Null(conditions.Band);
    }

    [Fact]
    public void OffSeasonPiecesStillWorkButScoreLower()
    {
        var summer = WeatherRules.FromSeason(new DateOnly(2026, 7, 1));

        Assert.Equal(1.0, WeatherRules.PieceFit(Piece("ust-giyim/tisort", ProductColor.White, seasons: Season.Summer), summer));
        Assert.Equal(WeatherRules.OffSeasonScore, WeatherRules.PieceFit(Piece("ust-giyim/tisort", ProductColor.White, seasons: Season.Winter), summer));
    }

    // ---- occasions ----

    [Fact]
    public void EveryOccasionHasAPresetWithASaneBandAndTarget()
    {
        foreach (var occasion in Enum.GetValues<Occasion>())
        {
            var preset = Occasions.Presets[occasion];

            Assert.InRange(preset.Min, 1, 5);
            Assert.InRange(preset.Max, preset.Min, 5);
            Assert.InRange(preset.Target, preset.Min, preset.Max);
            Assert.All(preset.Families.Values, weight => Assert.InRange(weight, 0, 1));
        }
    }

    [Theory]
    [InlineData("Office", "Classic", "Sporty")]
    [InlineData("Weekend", "Street", "Formal")]
    [InlineData("Evening", "Edgy", "Sporty")]
    [InlineData("Sport", "Sporty", "Formal")]
    [InlineData("Event", "Formal", "Street")]
    public void OccasionsFavourTheirFamilies(string occasionName, string favouredName, string outOfPlaceName)
    {
        var occasion = Enum.Parse<Occasion>(occasionName);
        var favoured = Enum.Parse<StyleFamily>(favouredName);
        var outOfPlace = Enum.Parse<StyleFamily>(outOfPlaceName);

        var families = Occasions.Presets[occasion].Families;

        Assert.Equal(1.0, families[favoured]);
        Assert.True(families.GetValueOrDefault(outOfPlace) < 0.5);
    }

    [Fact]
    public void AnOfficeOutfitCannotBeSporty()
    {
        var office = Context(Occasion.Office);
        var sport = Context(Occasion.Sport);

        var smart = OutfitScoring.Evaluate([Shirt(ProductColor.White, 3), Trousers(ProductColor.Navy, 3), Loafer(ProductColor.Brown, 4)], office);
        var tracksuit = OutfitScoring.Evaluate([Tee(ProductColor.Black, 1), Jeans(ProductColor.Black, 1), Sneaker(ProductColor.White, 1)], office);

        Assert.True(smart.Allowed);
        Assert.False(tracksuit.Allowed);

        // Sport also asks for sneakers.
        Assert.False(OutfitScoring.Evaluate([Tee(ProductColor.Black, 1), Jeans(ProductColor.Black, 1), Boot(ProductColor.Brown, 1)], sport).Allowed);
        Assert.True(OutfitScoring.Evaluate([Tee(ProductColor.Black, 1), Jeans(ProductColor.Black, 1), Sneaker(ProductColor.White, 1)], sport).Allowed);
    }

    // ---- slots ----

    [Theory]
    [InlineData("ust-giyim/gomlek", "Top")]
    [InlineData("ust-giyim/blazer", "Outerwear")]
    [InlineData("alt-giyim/etek", "Bottom")]
    [InlineData("dis-giyim/mont", "Outerwear")]
    [InlineData("ayakkabi/loafer", "Shoes")]
    [InlineData("aksesuar/canta", "Accessory")]
    public void ABlazerIsWornAsTheLayer(string slug, string slotName)
    {
        var slot = Enum.Parse<OutfitSlot>(slotName);

        Assert.Equal(slot, OutfitSlots.ForCategorySlug(slug));
    }

    private static WeatherSnapshot Forecast(double min, double max) => new("İstanbul", Day, min, max, 10, 0, 1);
}
