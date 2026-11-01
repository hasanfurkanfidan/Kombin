using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence.Configurations.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Tests;

public class SizeRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ust-giyim", "Tops")]
    [InlineData("ust-giyim/tisort", "Tops")]
    [InlineData("alt-giyim/jean", "Bottoms")]
    [InlineData("dis-giyim/mont", "Outerwear")]
    [InlineData("ayakkabi/sneaker", "Shoes")]
    [InlineData("aksesuar/canta", "Accessories")]
    [InlineData("something-new/else", "Accessories")]
    public void ACategoryMapsToItsTopLevelSizeGroup(string slug, string expected)
    {
        Assert.Equal(Enum.Parse<SizeGroup>(expected), SizeGroups.ForCategorySlug(slug));
    }

    [Fact]
    public void ASubcategoryAlwaysSharesTheGroupOfItsRoot()
    {
        foreach (var category in CatalogSeed.Categories())
        {
            var root = category.Slug.Split('/')[0];

            Assert.Equal(SizeGroups.ForCategorySlug(root), SizeGroups.ForCategorySlug(category.Slug));
        }
    }

    [Theory]
    [InlineData("m", "M")]
    [InlineData("  xl ", "XL")]
    [InlineData("38", "38")]
    [InlineData("42/44", "42/44")]
    public void SizesAreTrimmedAndUpperCased(string input, string expected)
    {
        Assert.Equal(expected, SizeGroups.NormalizeSize(input));
    }

    [Fact]
    public void TwoAgreeingEntriesMakeASuggestion()
    {
        var suggestion = SizeInference.Suggest([new("M", Now), new("M", Now.AddDays(-3))]);

        Assert.NotNull(suggestion);
        Assert.Equal("M", suggestion.Size);
        Assert.Equal(SizeSource.Inferred, suggestion.Source);
        Assert.Equal(0.8m, suggestion.Confidence);
    }

    [Fact]
    public void OneEntryIsNotEnough()
    {
        Assert.Null(SizeInference.Suggest([new("M", Now)]));
        Assert.Null(SizeInference.Suggest([]));
    }

    [Fact]
    public void EntriesThatDisagreeMakeNoSuggestion()
    {
        Assert.Null(SizeInference.Suggest([new("M", Now), new("L", Now), new("S", Now)]));
    }

    [Fact]
    public void TheMostCommonSizeWinsAndConfidenceReflectsTheShareThatAgrees()
    {
        var suggestion = SizeInference.Suggest([new("L", Now), new("M", Now), new("M", Now), new("L", Now), new("M", Now)]);

        Assert.NotNull(suggestion);
        Assert.Equal("M", suggestion.Size);
        Assert.Equal(0.6m, suggestion.Confidence);
    }

    [Fact]
    public void ATieGoesToTheMostRecentlyEnteredSize()
    {
        var suggestion = SizeInference.Suggest([new("M", Now.AddDays(-30)), new("M", Now.AddDays(-20)), new("L", Now), new("L", Now.AddDays(-1))]);

        Assert.NotNull(suggestion);
        Assert.Equal("L", suggestion.Size);
    }
}
