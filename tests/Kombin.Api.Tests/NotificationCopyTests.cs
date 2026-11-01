using Kombin.Api.Features.Notification;
using Kombin.Api.Persistence.Entities.Engagement;

namespace Kombin.Api.Tests;

public class NotificationCopyTests
{
    private static AlertCopyInput Blazer(decimal? before, decimal? after, params string[] sizes) =>
        new("Blazer", "Mavi", "Yün Karışımlı Blazer", before, after, sizes);

    [Theory]
    [InlineData(850, "850 ₺")]
    [InlineData(2149, "2.149 ₺")]
    [InlineData(849.9, "849,90 ₺")]
    [InlineData(1234567.5, "1.234.567,50 ₺")]
    [InlineData(0, "0 ₺")]
    [InlineData(0.05, "0,05 ₺")]
    [InlineData(999.999, "1.000 ₺")]
    [InlineData(1000, "1.000 ₺")]
    [InlineData(-1250.25, "-1.250,25 ₺")]
    public void MoneyIsWrittenTheTurkishWay(double amount, string expected) =>
        Assert.Equal(expected, Copy.Money((decimal)amount));

    [Theory]
    [InlineData("BLAZER", "blazer")]
    [InlineData("ISPARTA", "ısparta")]
    [InlineData("İZMİR", "izmir")]
    [InlineData("Üst Giyim", "üst giyim")]
    [InlineData("Tişört", "tişört")]
    public void LowerCasingFollowsTheTurkishRules(string value, string expected) =>
        Assert.Equal(expected, Copy.TurkishLower(value));

    [Fact]
    public void ThePriceDropCopyIsTheExampleFromTheSpec()
    {
        var text = Copy.ForAlert(AlertKind.PriceDrop, Blazer(2999m, 2149m));

        Assert.Equal("Takip ettiğin blazer 850 ₺ düştü", text.Title);
        Assert.Equal("Mavi Yün Karışımlı Blazer artık 2.149 ₺ — hedef fiyatının altında.", text.Body);
    }

    [Fact]
    public void APriceDropThatCameBackInStockWithoutADropStillReadsWell()
    {
        var text = Copy.ForAlert(AlertKind.PriceDrop, Blazer(2149m, 2149m));

        Assert.Equal("Takip ettiğin blazer hedef fiyatına indi", text.Title);
        Assert.Equal("Mavi Yün Karışımlı Blazer artık 2.149 ₺ — hedef fiyatının altında.", text.Body);
    }

    [Fact]
    public void ABrandAlreadyInTheProductNameIsNotRepeated() =>
        Assert.Equal("Mavi Yün Blazer", Copy.ProductDisplayName("Mavi", "Mavi Yün Blazer"));

    [Fact]
    public void BackInStockNamesTheSizesAndThePrice()
    {
        var text = Copy.ForAlert(AlertKind.BackInStock, Blazer(2149m, 2149.5m, "M", "L"));

        Assert.Equal("Takip ettiğin blazer tekrar stokta", text.Title);
        Assert.Equal("Mavi Yün Karışımlı Blazer yeniden satışta — M, L bedenleri mevcut, fiyatı 2.149,50 ₺.", text.Body);
    }

    [Fact]
    public void BackInStockWithoutSizesLeavesThemOut()
    {
        var text = Copy.ForAlert(AlertKind.BackInStock, Blazer(null, null));

        Assert.Equal("Mavi Yün Karışımlı Blazer yeniden satışta.", text.Body);
    }

    [Fact]
    public void AnyChangeSaysWhetherThePriceFellOrRose()
    {
        var fell = Copy.ForAlert(AlertKind.AnyChange, Blazer(1000m, 900m));
        var rose = Copy.ForAlert(AlertKind.AnyChange, Blazer(1000m, 1100m));

        Assert.Equal("Takip ettiğin blazer 100 ₺ düştü", fell.Title);
        Assert.Equal("Mavi Yün Karışımlı Blazer artık 900 ₺ (önceki fiyat 1.000 ₺).", fell.Body);
        Assert.Equal("Takip ettiğin blazer 100 ₺ zamlandı", rose.Title);
        Assert.Equal("Mavi Yün Karışımlı Blazer şimdi 1.100 ₺ (önceki fiyat 1.000 ₺).", rose.Body);
    }

    [Fact]
    public void AnyChangeWithoutAPriceChangeIsAboutStock()
    {
        var text = Copy.ForAlert(AlertKind.AnyChange, Blazer(1000m, 1000m));

        Assert.Equal("Takip ettiğin blazer için stok durumu değişti", text.Title);
        Assert.Equal("Mavi Yün Karışımlı Blazer için stok bilgisi güncellendi.", text.Body);
    }

    [Theory]
    [InlineData(93, 3, "\"Hafta sonu\" tarzına %90 üzeri uyumlu 3 yeni parça")]
    [InlineData(70, 1, "\"Hafta sonu\" tarzına %70 üzeri uyumlu 1 yeni parça")]
    [InlineData(100, 2, "\"Hafta sonu\" tarzına %100 üzeri uyumlu 2 yeni parça")]
    public void TheWeeklyDigestTitleRoundsTheLowestScoreDownToTens(int lowestScore, int count, string expected) =>
        Assert.Equal(expected, Copy.ForNewMatches("Hafta sonu", count, lowestScore, ["A", "B"]).Title);

    [Fact]
    public void TheWeeklyDigestBodyListsTheProducts() =>
        Assert.Equal("Mavi Blazer, Koton Triko", Copy.ForNewMatches("Ofis", 2, 80, ["Mavi Blazer", "Koton Triko"]).Body);

    [Fact]
    public void TextIsClippedToTheColumnLengths()
    {
        var input = new AlertCopyInput(new string('k', 200), new string('b', 200), new string('p', 400), 100m, 50m, []);

        var text = Copy.ForAlert(AlertKind.PriceDrop, input);

        Assert.Equal(Copy.MaxTitleLength, text.Title.Length);
        Assert.Equal(Copy.MaxBodyLength, text.Body.Length);
        Assert.EndsWith("…", text.Title, StringComparison.Ordinal);
    }
}
