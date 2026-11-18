using System.Text.RegularExpressions;
using Kombin.Api.Common.Text;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Tests;

/// <summary>
/// Every Turkish line of bulk add, pinned (task BA2). The refusal and reason sentences are the vision team's, copied verbatim from
/// <c>kombin-vision/docs/design/bulk-add-briefs.md</c>: changing one here is a decision, not a typo fix.
/// </summary>
public sealed partial class BulkAddCopyTests
{
    public static TheoryData<string, string> TheBriefsRefusals => new()
    {
        { "closet", "Dolabın içini değil, kıyafetleri yere ya da yatağa serip yukarıdan çek." },
        { "rail", "Askıdaki kıyafetleri ayıramıyoruz; yere ya da yatağa serip yukarıdan çek." },
        { "pile", "Kıyafetler üst üste yığılmış; aralarında boşluk bırakarak ser ve tekrar çek." },
        { "worn", "Üzerindeki kıyafetleri değil, serilmiş kıyafetleri çek." },
        { "nothing_found", "Fotoğrafta kıyafet bulamadık." },
        { "too_dark", "Fotoğraf çok karanlık; daha aydınlık bir yerde tekrar çek." },
        { "too_blurry", "Fotoğraf bulanık; telefonu sabit tutup tekrar çek." },
    };

    public static TheoryData<string, string> TheBriefsReasons => new()
    {
        { "partly_hidden", "Bu parçanın bir kısmı başka bir kıyafetin altında kalmış; istersen tek başına ekle." },
        { "touching", "Bu parça yanındakine değiyor; kesimini kontrol et." },
        { "cut_by_frame", "Bu parça fotoğrafın kenarından taşıyor; tamamı görünmüyor olabilir." },
        { "may_be_several", "Bu parça birden fazla kıyafet olabilir; kontrol et." },
        { "may_not_be_garment", "Bunun bir kıyafet olduğundan emin değiliz; kontrol et." },
        { "too_small", "Bu parça çok küçük göründü; istersen yakından tek başına ekle." },
        { "unsure_outline", "Bu parçanın kenarlarını net ayıramadık; kesimini kontrol et." },
    };

    [Theory]
    [MemberData(nameof(TheBriefsRefusals))]
    public void EveryRefusalSaysTheBriefsSentence(string code, string sentence) => Assert.Equal(sentence, BulkAddCopy.ForRefusal(code));

    [Theory]
    [MemberData(nameof(TheBriefsReasons))]
    public void EveryReasonSaysTheBriefsSentence(string code, string sentence) => Assert.Equal(sentence, BulkAddCopy.ForReason(code));

    [Fact]
    public void ThereAreExactlyTheBriefsSevenRefusalsAndSevenReasons()
    {
        Assert.Equal(7, BulkAddCopy.Refusals.Count);
        Assert.Equal(7, BulkAddCopy.Reasons.Count);
        Assert.Equal(TheBriefsRefusals.Select(row => row.Data.Item1).Order(StringComparer.Ordinal), BulkAddCopy.Refusals.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(TheBriefsReasons.Select(row => row.Data.Item1).Order(StringComparer.Ordinal), BulkAddCopy.Reasons.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("something_new")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownReasonFromANewerVisionIsACheckNotAnError(string? code) => Assert.Equal("Bu parçayı kontrol et.", BulkAddCopy.ForReason(code));

    [Theory]
    [InlineData("something_new")]
    [InlineData(null)]
    public void AnUnknownRefusalStillGetsAdvice(string? code) =>
        Assert.Equal("Bu fotoğraftan parçaları ayıramadık; kıyafetleri yere ya da yatağa serip yukarıdan çekmeyi dene.", BulkAddCopy.ForRefusal(code));

    [Fact]
    public void TheOtherLinesArePinned()
    {
        Assert.Equal("Dolabında benzeri var.", BulkAddCopy.LikelyDuplicate);
        Assert.Equal("2 parçayı ayıramadık; onları tek tek ekleyebilirsin.", BulkAddCopy.Unseparated(2));
        Assert.Equal(
            "Fotoğrafta 3 parça daha vardı; bir seferde bu kadarını gösterebiliyoruz. Kalanları ayrı bir fotoğrafla ekleyebilirsin.",
            BulkAddCopy.CutOff(3));
    }

    [Fact]
    public void EveryFailureHasASentenceAndOnlyAnUnreadablePhotoIsNotRetryable()
    {
        Assert.Equal("Şu an çok yoğunuz; birazdan tekrar dene.", BulkAddCopy.ForFailure(BulkFailureCode.VisionBusy));
        Assert.Equal("Şu an çok yoğunuz; birazdan tekrar dene.", BulkAddCopy.ForFailure(BulkFailureCode.VisionUnavailable));
        Assert.Equal("Bu fotoğrafı açamadık; başka bir fotoğrafla dene.", BulkAddCopy.ForFailure(BulkFailureCode.PhotoUnreadable));
        Assert.Equal("Fotoğrafı işlerken bir şeyler ters gitti; tekrar dene.", BulkAddCopy.ForFailure(BulkFailureCode.AnalysisFailed));
        Assert.Equal("Fotoğrafı işlerken takıldık; tekrar dene.", BulkAddCopy.ForFailure(BulkFailureCode.Interrupted));

        foreach (var code in Enum.GetValues<BulkFailureCode>())
        {
            Assert.False(string.IsNullOrWhiteSpace(BulkAddCopy.ForFailure(code)), code.ToString());
            Assert.Equal(code != BulkFailureCode.PhotoUnreadable, BulkAddCopy.IsRetryable(code));
        }
    }

    [Fact]
    public void NoLineHasAnExclamationMarkAnEmojiOrABannedWord()
    {
        var lines = BulkAddCopy.Refusals.Values
            .Concat(BulkAddCopy.Reasons.Values)
            .Append(BulkAddCopy.UnknownReason)
            .Append(BulkAddCopy.UnknownRefusal)
            .Append(BulkAddCopy.LikelyDuplicate)
            .Append(BulkAddCopy.Unseparated(2))
            .Append(BulkAddCopy.CutOff(3))
            .Concat(Enum.GetValues<BulkFailureCode>().Select(BulkAddCopy.ForFailure))
            .ToList();

        foreach (var line in lines)
        {
            Assert.DoesNotContain('!', line);
            Assert.False(Emoji().IsMatch(line), $"'{line}' has an emoji.");
            Assert.Empty(CopyGlossary.Violations(line, aboutPeople: false));
        }
    }

    [Fact]
    public void NoLineNamesANumberThatIsASetting()
    {
        // 12 pieces, 8 photos, 3 open batches and 24 hours are settings: a line that says them would be wrong the day one is changed.
        // The only digits in the copy are the placeholders of a count the batch itself reports.
        var fixedLines = BulkAddCopy.Refusals.Values
            .Concat(BulkAddCopy.Reasons.Values)
            .Concat([BulkAddCopy.UnknownReason, BulkAddCopy.UnknownRefusal, BulkAddCopy.LikelyDuplicate])
            .Concat(Enum.GetValues<BulkFailureCode>().Select(BulkAddCopy.ForFailure));

        Assert.All(fixedLines, line => Assert.DoesNotContain(line, char.IsDigit));
    }

    [GeneratedRegex(@"\p{Cs}|[☀-➿]|️")]
    private static partial Regex Emoji();
}
