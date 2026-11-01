using Kombin.Api.Features.Engagement;
using Kombin.Api.Features.Engagement.Redirect;

namespace Kombin.Api.Tests;

public class AffiliateUrlBuilderTests
{
    private const string OfferUrl = "https://www.trendyol.com/mavi/blazer-p-123";

    [Fact]
    public void WithoutATemplateTheOfferUrlGetsTrackingParameters()
    {
        var url = AffiliateUrlBuilder.Build(OfferUrl, template: null, parameters: null, clickId: 42, source: "product_detail");

        Assert.Equal($"{OfferUrl}?utm_source=kombin&utm_medium=app&utm_campaign=product_detail&kombin_click=42", url);
    }

    [Fact]
    public void ABlankTemplateCountsAsNoTemplate()
    {
        var url = AffiliateUrlBuilder.Build(OfferUrl, template: "  ", parameters: null, clickId: 1, source: "feed");

        Assert.StartsWith($"{OfferUrl}?utm_source=kombin", url, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackingParametersAreMergedIntoTheExistingQueryStringAndKeepTheFragment()
    {
        var url = AffiliateUrlBuilder.Build($"{OfferUrl}?boutiqueId=61&merchantId=7#reviews", null, null, 7, "outfit");

        Assert.Equal(
            $"{OfferUrl}?boutiqueId=61&merchantId=7&utm_source=kombin&utm_medium=app&utm_campaign=outfit&kombin_click=7#reviews",
            url);
    }

    [Fact]
    public void ExistingValuesOfOurOwnParametersAreReplacedNotDuplicated()
    {
        var url = AffiliateUrlBuilder.Build($"{OfferUrl}?color=red&utm_source=other&kombin_click=1", null, null, 9, "saved");

        Assert.Equal($"{OfferUrl}?color=red&utm_source=kombin&utm_medium=app&utm_campaign=saved&kombin_click=9", url);
    }

    [Fact]
    public void TheOffersOwnQueryEncodingIsLeftAlone()
    {
        var url = AffiliateUrlBuilder.Build($"{OfferUrl}?q=a%20b&size=38%2F40", null, null, 3, "alert");

        Assert.Contains("?q=a%20b&size=38%2F40&utm_source=kombin", url, StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplateFillsInEveryPlaceholder()
    {
        const string template = "https://tracking.example/redirect?url={url}&subid={clickId}&aff={p.affId}&src={source}&raw={rawUrl}";
        var parameters = new Dictionary<string, string> { ["affId"] = "kombin-77" };

        var url = AffiliateUrlBuilder.Build($"{OfferUrl}?a=1&b=2", template, parameters, clickId: 15, source: "product_detail");

        Assert.Equal(
            "https://tracking.example/redirect"
            + $"?url={Uri.EscapeDataString($"{OfferUrl}?a=1&b=2")}&subid=15&aff=kombin-77&src=product_detail&raw={OfferUrl}?a=1&b=2",
            url);
    }

    [Fact]
    public void TheUrlPlaceholderIsUrlEncodedSoItCanNotLeakIntoTheTrackerQuery()
    {
        var url = AffiliateUrlBuilder.Build($"{OfferUrl}?a=1&b=2#x", "https://t.example/r?u={url}", null, 1, "feed");

        Assert.Equal("https://t.example/r?u=https%3A%2F%2Fwww.trendyol.com%2Fmavi%2Fblazer-p-123%3Fa%3D1%26b%3D2%23x", url);
    }

    [Fact]
    public void ParameterValuesAreUrlEncodedAndAMissingParameterBecomesEmpty()
    {
        var parameters = new Dictionary<string, string> { ["campaign"] = "kış & indirim" };

        var url = AffiliateUrlBuilder.Build(OfferUrl, "https://t.example/r?c={p.campaign}&x={p.missing}", parameters, 1, "feed");

        Assert.Equal("https://t.example/r?c=k%C4%B1%C5%9F%20%26%20indirim&x=", url);
    }

    [Fact]
    public void AnUnknownPlaceholderIsLeftAsWritten()
    {
        var url = AffiliateUrlBuilder.Build(OfferUrl, "https://t.example/r?u={url}&z={nope}", null, 1, "feed");

        Assert.EndsWith("&z={nope}", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url {url}")]
    [InlineData("javascript:alert({clickId})")]
    [InlineData("/relative?u={url}")]
    public void ATemplateThatDoesNotProduceAnAbsoluteHttpUrlFallsBackToTheDefault(string template)
    {
        var url = AffiliateUrlBuilder.Build(OfferUrl, template, null, 5, "feed");

        Assert.Equal($"{OfferUrl}?utm_source=kombin&utm_medium=app&utm_campaign=feed&kombin_click=5", url);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("nonsense", "unknown")]
    [InlineData("product_detail", "product_detail")]
    [InlineData("PRODUCT_DETAIL", "product_detail")]
    [InlineData(" outfit ", "outfit")]
    [InlineData("saved", "saved")]
    [InlineData("alert", "alert")]
    [InlineData("feed", "feed")]
    [InlineData("unknown", "unknown")]
    public void SourcesOutsideTheAllowedListAreRecordedAsUnknown(string? source, string expected)
    {
        Assert.Equal(expected, ClickSources.Normalize(source));
    }

    [Fact]
    public void TheIpHashIsASaltedSha256InHexAndNeverTheAddress()
    {
        var hash = RedirectEndpoint.HashIp("203.0.113.9", "pepper");

        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("203", hash, StringComparison.Ordinal);
        Assert.Equal(hash, RedirectEndpoint.HashIp("203.0.113.9", "pepper"));
        Assert.NotEqual(hash, RedirectEndpoint.HashIp("203.0.113.9", "other-pepper"));
        Assert.NotEqual(hash, RedirectEndpoint.HashIp("203.0.113.10", "pepper"));
    }

    [Fact]
    public void AnUnknownAddressHasNoHash()
    {
        Assert.Null(RedirectEndpoint.HashIp(null, "pepper"));
        Assert.Null(RedirectEndpoint.HashIp(string.Empty, "pepper"));
    }
}
