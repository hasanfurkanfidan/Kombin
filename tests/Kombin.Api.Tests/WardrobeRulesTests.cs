using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Features.Wardrobe.AddFromProduct;
using Kombin.Api.Features.Wardrobe.CreateUploadUrl;
using Kombin.Api.Features.Wardrobe.UpdateItem;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Tests;

public class WardrobeRulesTests
{
    private static VisionAnalysis Analysis(
        VisionCategory? category,
        IReadOnlyList<VisionCategory>? candidates = null,
        string color = "Red",
        string? secondary = null,
        string pattern = "Solid",
        bool isGarment = true,
        string[]? warnings = null) =>
        new(
            "Y3V0b3V0",
            "dGh1bWI=",
            category,
            candidates ?? (category is null ? [] : [category]),
            new VisionColor(color, 0.8, "#C51D27"),
            secondary is null ? null : new VisionColor(secondary, 0.3, "#FFFFFF"),
            new VisionPattern(pattern, 0.9),
            new float[512],
            new VisionQuality(isGarment, 0.9, warnings ?? []));

    [Theory]
    [InlineData(0.35, "ust-giyim/tisort")]
    [InlineData(0.9995, "ust-giyim/tisort")]
    [InlineData(0.3499, null)]
    [InlineData(0.05, null)]
    public void TheCategoryIsTakenOnlyAtOrAboveTheConfidenceThreshold(double confidence, string? expected)
    {
        var attributes = WardrobeAnalysis.Map(Analysis(new VisionCategory("ust-giyim/tisort", confidence)));

        Assert.Equal(expected, attributes.CategorySlug);
    }

    [Fact]
    public void WithoutACategoryTheTopCandidateIsJudgedAndTheDefaultsStayNeutral()
    {
        var sure = WardrobeAnalysis.Map(Analysis(null, [new VisionCategory("ayakkabi/sneaker", 0.7), new VisionCategory("ayakkabi/bot", 0.2)]));
        Assert.Equal("ayakkabi/sneaker", sure.CategorySlug);

        var unsure = WardrobeAnalysis.Map(Analysis(null, []));
        Assert.Null(unsure.CategorySlug);
        Assert.Equal(3, unsure.Formality);
        Assert.Equal(Season.AllYear, unsure.Seasons);
    }

    [Fact]
    public void ColorsAndPatternMapToTheCatalogEnumsAndUnknownNamesBecomeOther()
    {
        var known = WardrobeAnalysis.Map(Analysis(null, color: "navy", secondary: "White", pattern: "Striped"));
        Assert.Equal(ProductColor.Navy, known.PrimaryColor);
        Assert.Equal(ProductColor.White, known.SecondaryColor);
        Assert.Equal(ProductPattern.Striped, known.Pattern);

        var unknown = WardrobeAnalysis.Map(Analysis(null, color: "Turquoise", pattern: "Paisley"));
        Assert.Equal(ProductColor.Other, unknown.PrimaryColor);
        Assert.Null(unknown.SecondaryColor);
        Assert.Equal(ProductPattern.Other, unknown.Pattern);
    }

    [Fact]
    public void ANonGarmentPhotoIsFlaggedFirstAndTheServiceWarningsFollow()
    {
        Assert.Equal(["not_garment", "person_wearing"], WardrobeAnalysis.Map(Analysis(null, isGarment: false, warnings: ["person_wearing"])).Warnings);
        Assert.Equal(["blurry"], WardrobeAnalysis.Map(Analysis(null, warnings: ["blurry", "blurry"])).Warnings);
        Assert.Empty(WardrobeAnalysis.Map(Analysis(null)).Warnings);
    }

    [Theory]
    [InlineData("ust-giyim/tisort", 2, "Spring, Summer, Autumn, Winter")]
    [InlineData("ust-giyim/blazer", 4, "Spring, Summer, Autumn, Winter")]
    [InlineData("dis-giyim/kaban", 3, "Autumn, Winter")]
    [InlineData("dis-giyim/mont", 2, "Autumn, Winter")]
    [InlineData("ust-giyim/triko", 3, "Spring, Autumn, Winter")]
    [InlineData(null, 3, "Spring, Summer, Autumn, Winter")]
    public void TheCategoryGivesAFirstGuessAtFormalityAndSeasons(string? slug, short formality, string seasons)
    {
        var (actualFormality, actualSeasons) = WardrobeAnalysis.Defaults(slug);

        Assert.Equal(formality, actualFormality);
        Assert.Equal(seasons, string.Join(", ", Kombin.Api.Features.Catalog.EnumNames.SeasonNames(actualSeasons)));
    }

    [Fact]
    public void TheStoredAnalysisLeavesOutTheImagesAndTheEmbeddingAndReadsBack()
    {
        var analysis = Analysis(new VisionCategory("ust-giyim/tisort", 0.9), warnings: ["dark"]);

        var json = WardrobeAnalysis.ToJson(analysis, "ViT-B-32/laion2b_s34b_b79k", new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));

        Assert.DoesNotContain("Y3V0b3V0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("embedding\":", json, StringComparison.Ordinal);

        var stored = WardrobeAnalysis.Read(json);
        Assert.NotNull(stored);
        Assert.Equal("ust-giyim/tisort", stored.Category!.Slug);
        Assert.Equal(0.8, stored.PrimaryColor.Confidence);
        Assert.Equal("ViT-B-32/laion2b_s34b_b79k", stored.EmbeddingModel);
        Assert.Equal(["dark"], stored.Quality.Warnings);

        Assert.Null(WardrobeAnalysis.Read(null));
        Assert.Null(WardrobeAnalysis.Read("not json"));
    }

    [Fact]
    public void StorageKeysFollowTheAreaUserOwnerVariantConvention()
    {
        var userId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var itemId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.Equal(
            "wardrobe/11111111-2222-3333-4444-555555555555/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/original.jpg",
            StorageKeys.Build("wardrobe", userId, itemId, ImageVariant.Original, "jpg"));
        Assert.Equal(
            "wardrobe/11111111-2222-3333-4444-555555555555/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/thumb.webp",
            StorageKeys.Build("wardrobe", userId, itemId, ImageVariant.Thumb, ".WEBP"));
    }

    [Theory]
    [InlineData("wardrobe/u/i/cutout.png", true)]
    [InlineData("wardrobe/u/i/thumb.webp", true)]
    [InlineData("wardrobe/u/i/original.jpg", false)]
    [InlineData("wardrobe/u/i/mythumb.webp", false)]
    public void OnlyCutoutsAndThumbnailsArePublic(string key, bool expected)
    {
        Assert.Equal(expected, StorageKeys.IsPublic(key));
    }

    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("IMAGE/PNG", "png")]
    [InlineData("image/webp; charset=binary", "webp")]
    [InlineData("image/heic", "heic")]
    [InlineData("image/gif", null)]
    [InlineData("application/pdf", null)]
    [InlineData(null, null)]
    public void OnlyTheAcceptedImageTypesHaveAnExtension(string? contentType, string? expected)
    {
        Assert.Equal(expected, StorageKeys.ExtensionFor(contentType));
    }

    [Fact]
    public void AWearDefaultsToTodayAndRefusesTheFutureAndTheDistantPast()
    {
        var today = new DateOnly(2026, 11, 2);

        Assert.Equal((today, (string?)null), WearRules.Resolve(null, today));
        Assert.Equal((today.AddDays(-3), (string?)null), WearRules.Resolve(today.AddDays(-3), today));
        Assert.NotNull(WearRules.Resolve(today.AddDays(1), today).Error);
        Assert.NotNull(WearRules.Resolve(new DateOnly(1999, 12, 31), today).Error);
    }

    [Fact]
    public void TodayIsTheIstanbulCalendarDay()
    {
        // 22:30 UTC on 1 November is already 01:30 on 2 November in Istanbul (UTC+3).
        Assert.Equal(new DateOnly(2026, 11, 2), WearRules.Today(new DateTimeOffset(2026, 11, 1, 22, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 1), WearRules.Today(new DateTimeOffset(2026, 11, 1, 20, 59, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TheWearSummaryCountsDistinctDaysAndKeepsTheLatest()
    {
        var day = new DateOnly(2026, 10, 30);

        Assert.Equal((0, (DateOnly?)null), WearRules.Summarize([]));
        Assert.Equal((2, (DateOnly?)day.AddDays(2)), WearRules.Summarize([day, day.AddDays(2), day]));
    }

    [Theory]
    [InlineData("image/jpeg", 1024L, true)]
    [InlineData("image/heic", 8L * 1024 * 1024, true)]
    [InlineData("image/png", 8L * 1024 * 1024 + 1, false)]
    [InlineData("image/png", 0L, false)]
    [InlineData("image/gif", 1024L, false)]
    [InlineData(null, 1024L, false)]
    public void AnUploadMustBeAnAcceptedImageOfAtMostEightMegabytes(string? contentType, long size, bool valid)
    {
        Assert.Equal(valid, new CreateUploadUrlValidator().Validate(new CreateUploadUrlRequest(contentType, size)).IsValid);
    }

    private static UpdateItemRequest Patch(
        string? name = null, string? primaryColor = null, short? formality = null, IReadOnlyList<string>? seasons = null, string? size = null, IReadOnlyList<string>? clear = null) =>
        new(name, null, primaryColor, null, null, null, formality, seasons, null, size, null, null, clear);

    [Fact]
    public void AnEmptyPatchIsTheOneTapConfirmation()
    {
        Assert.True(new UpdateItemValidator().Validate(Patch()).IsValid);
    }

    [Fact]
    public void APatchChecksEnumNamesRangesAndTheClearList()
    {
        var validator = new UpdateItemValidator();

        Assert.True(validator.Validate(Patch(primaryColor: "beige", formality: 4, seasons: ["Winter"], clear: ["secondaryColor", "brandId"])).IsValid);
        Assert.False(validator.Validate(Patch(primaryColor: "Turquoise")).IsValid);
        Assert.False(validator.Validate(Patch(formality: 6)).IsValid);
        Assert.False(validator.Validate(Patch(seasons: [])).IsValid);
        Assert.False(validator.Validate(Patch(seasons: ["AllYear"])).IsValid);
        Assert.False(validator.Validate(Patch(name: "  ")).IsValid);
        Assert.False(validator.Validate(Patch(name: new string('a', 101))).IsValid);
        Assert.False(validator.Validate(Patch(clear: ["category"])).IsValid);

        var both = validator.Validate(Patch(size: "M", clear: ["size"]));
        Assert.False(both.IsValid);
        Assert.Contains(both.Errors, error => error.PropertyName == "clear");
    }

    [Fact]
    public void AddingFromAProductNeedsTheProductAndAShortSize()
    {
        var validator = new AddFromProductValidator();

        Assert.True(validator.Validate(new AddFromProductRequest(Guid.NewGuid(), "M")).IsValid);
        Assert.False(validator.Validate(new AddFromProductRequest(Guid.Empty, null)).IsValid);
        Assert.False(validator.Validate(new AddFromProductRequest(Guid.NewGuid(), new string('X', 21))).IsValid);
    }
}
