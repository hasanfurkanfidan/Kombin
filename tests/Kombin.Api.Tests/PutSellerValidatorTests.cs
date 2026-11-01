using Kombin.Api.Features.Catalog.PutSeller;

namespace Kombin.Api.Tests;

public class PutSellerValidatorTests
{
    private readonly PutSellerValidator _validator = new();

    private readonly PutSellerRouteValidator _routeValidator = new();

    private static PutSellerRequest Valid() =>
        new("Mavi Store", "BrandStore", "https://www.mavi.com", null, null, null);

    [Fact]
    public void MinimalSellerPasses()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void FullSellerPasses()
    {
        var request = Valid() with
        {
            AffiliateNetwork = "awin",
            AffiliateUrlTemplate = "https://track.example.com/c?aid={p.aid}&u={url}&sub={clickId}",
            AffiliateParams = new Dictionary<string, string> { ["aid"] = "12345", ["campaign.name"] = "kombin" },
            IsActive = false,
        };

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void ASellerIsActiveUnlessSaidOtherwise()
    {
        Assert.True(Valid().IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NameIsRequired(string name)
    {
        var result = _validator.Validate(Valid() with { Name = name });

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Name");
    }

    [Theory]
    [InlineData("marketplace")]
    [InlineData("BRANDSTORE")]
    public void KindIsCaseInsensitive(string kind)
    {
        Assert.True(_validator.Validate(Valid() with { Kind = kind }).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("Marketplace,BrandStore")]
    [InlineData("Outlet")]
    public void KindMustBeASingleDefinedName(string kind)
    {
        var result = _validator.Validate(Valid() with { Kind = kind });

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Kind");
    }

    [Theory]
    [InlineData("")]
    [InlineData("www.mavi.com")]
    [InlineData("ftp://mavi.com")]
    [InlineData("https://user:pw@mavi.com")]
    public void WebsiteMustBeAnAbsoluteHttpUrl(string url)
    {
        var result = _validator.Validate(Valid() with { WebsiteUrl = url });

        Assert.Contains(result.Errors, failure => failure.PropertyName == "WebsiteUrl");
    }

    [Fact]
    public void ATemplateMustCarryTheOfferUrl()
    {
        var result = _validator.Validate(Valid() with { AffiliateUrlTemplate = "https://track.example.com/c?aid=1" });

        Assert.Contains(result.Errors, failure => failure.PropertyName == "AffiliateUrlTemplate");
    }

    [Theory]
    [InlineData("https://track.example.com/c?u={url}")]
    [InlineData("https://track.example.com/c?u={rawUrl}")]
    public void EitherUrlPlaceholderWillDo(string template)
    {
        Assert.True(_validator.Validate(Valid() with { AffiliateUrlTemplate = template }).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1st")]
    [InlineData("has space")]
    [InlineData("curly{}")]
    public void ParamKeysMustBeUsableAsPlaceholders(string key)
    {
        var request = Valid() with { AffiliateParams = new Dictionary<string, string> { [key] = "x" } };

        var result = _validator.Validate(request);

        Assert.Contains(result.Errors, failure => failure.PropertyName == "AffiliateParams");
    }

    [Fact]
    public void ParamValuesMayNotBeNullOrHuge()
    {
        var nullValue = Valid() with { AffiliateParams = new Dictionary<string, string> { ["aid"] = null! } };
        var hugeValue = Valid() with { AffiliateParams = new Dictionary<string, string> { ["aid"] = new string('x', 201) } };

        Assert.False(_validator.Validate(nullValue).IsValid);
        Assert.False(_validator.Validate(hugeValue).IsValid);
    }

    [Fact]
    public void TooManyParamsAreRejected()
    {
        var parameters = Enumerable.Range(0, 21).ToDictionary(index => $"k{index}", _ => "v");

        var result = _validator.Validate(Valid() with { AffiliateParams = parameters });

        Assert.Contains(result.Errors, failure => failure.PropertyName == "AffiliateParams");
    }

    [Fact]
    public void ASlugInTheRouteIsKebabCase()
    {
        Assert.True(_routeValidator.Validate(new PutSellerRoute("mavi-store")).IsValid);
    }

    [Theory]
    [InlineData("Mavi")]
    [InlineData("mavi store")]
    [InlineData("mavi--store")]
    [InlineData("-mavi")]
    public void ASlugInTheRouteThatIsNotKebabCaseIsRejected(string slug)
    {
        var result = _routeValidator.Validate(new PutSellerRoute(slug));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "SellerSlug");
    }
}
