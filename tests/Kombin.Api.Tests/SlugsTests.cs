using Kombin.Api.Features.Catalog;

namespace Kombin.Api.Tests;

public class SlugsTests
{
    [Theory]
    [InlineData("Mavi Wool-Blend Blazer", "mavi-wool-blend-blazer")]
    [InlineData("  Spaces   and --- dashes  ", "spaces-and-dashes")]
    [InlineData("U.S. Polo Assn.", "u-s-polo-assn")]
    [InlineData("Size 42/44", "size-42-44")]
    public void SlugifyProducesLowerCaseKebabCase(string text, string expected)
    {
        Assert.Equal(expected, Slugs.Slugify(text));
    }

    [Theory]
    [InlineData("çğıöşü", "cgiosu")]
    [InlineData("ÇĞİÖŞÜ", "cgiosu")]
    [InlineData("ISPARTA", "isparta")]
    [InlineData("Şık Çanta", "sik-canta")]
    [InlineData("Gömlek", "gomlek")]
    [InlineData("Ağaç Işığı", "agac-isigi")]
    [InlineData("Crème Brûlée", "creme-brulee")]
    public void SlugifyTransliteratesTurkishAndCommonAccentedLetters(string text, string expected)
    {
        Assert.Equal(expected, Slugs.Slugify(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ---")]
    [InlineData("日本語")]
    public void SlugifyReturnsEmptyWhenNothingUsableRemains(string? text)
    {
        Assert.Equal(string.Empty, Slugs.Slugify(text));
    }

    [Fact]
    public void MakeUniqueKeepsAFreeSlug()
    {
        Assert.Equal("blazer", Slugs.MakeUnique("blazer", new HashSet<string> { "jean" }));
    }

    [Fact]
    public void MakeUniqueAppendsTheFirstFreeNumber()
    {
        var taken = new HashSet<string> { "blazer", "blazer-2", "blazer-4" };

        Assert.Equal("blazer-3", Slugs.MakeUnique("blazer", taken));
    }

    [Fact]
    public void MakeUniqueSkipsEverySuffixThatIsTaken()
    {
        var taken = new HashSet<string> { "blazer", "blazer-2", "blazer-3", "blazer-4" };

        Assert.Equal("blazer-5", Slugs.MakeUnique("blazer", taken));
    }
}
