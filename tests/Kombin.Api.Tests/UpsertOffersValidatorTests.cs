using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Catalog.UpsertOffers;

namespace Kombin.Api.Tests;

public class UpsertOffersValidatorTests
{
    private readonly UpsertOffersValidator _validator = new();

    private static UpsertProductInfo ValidProduct() =>
        new("Mavi", "Wool-blend blazer", "M-100", "ust-giyim/blazer", "Women", "Green", null, null, null, null, null, null, null);

    private static UpsertOfferItem ValidItem(string externalId = "ext-1") =>
        new(externalId, "https://www.trendyol.com/p/1", "Mavi blazer", 199.90m, 299.90m, "TRY", 29.90m, 500m, "InStock", ["S", "M"], ["https://img.example.com/1.jpg"], ValidProduct());

    private static UpsertOffersRequest Request(params UpsertOfferItem[] items) => new("trendyol", items);

    private static UpsertOffersRequest RequestWith(int count) =>
        Request([.. Enumerable.Range(0, count).Select(index => ValidItem($"ext-{index}"))]);

    [Fact]
    public void ValidRequestPasses()
    {
        Assert.True(_validator.Validate(Request(ValidItem(), ValidItem("ext-2"))).IsValid);
    }

    [Fact]
    public void MinimalItemPasses()
    {
        var item = ValidItem() with
        {
            OriginalPrice = null,
            Currency = null,
            ShippingCost = null,
            FreeShippingThreshold = null,
            AvailableSizes = null,
            ImageUrls = null,
        };

        Assert.True(_validator.Validate(Request(item)).IsValid);
    }

    [Fact]
    public void NestedItemErrorsAreKeyedByIndex()
    {
        var items = new[] { ValidItem("a"), ValidItem("b"), ValidItem("c"), ValidItem("d") with { Price = -1m }, ValidItem("e") };

        var result = _validator.Validate(Request(items));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("Items[3].Price", failure.PropertyName);
    }

    [Fact]
    public void NestedProductErrorsAreKeyedByIndexAndProperty()
    {
        var item = ValidItem() with { Product = ValidProduct() with { Audience = "Aliens", Formality = 9 } };

        var result = _validator.Validate(Request(ValidItem(), item));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[1].Product.Audience");
        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[1].Product.Formality");
    }

    [Fact]
    public void ElementsOfNestedListsAreKeyedByIndex()
    {
        var item = ValidItem() with { ImageUrls = ["https://ok.example.com/a.jpg", "not-a-url"], Product = ValidProduct() with { Seasons = ["Spring", "Monsoon"] } };

        var result = _validator.Validate(Request(item));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].ImageUrls[1]");
        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Product.Seasons[1]");
    }

    [Fact]
    public void AnItemMayCarryUpToSixImages()
    {
        var urls = Enumerable.Range(1, 6).Select(number => $"https://img.example.com/{number}.jpg").ToArray();

        Assert.True(_validator.Validate(Request(ValidItem() with { ImageUrls = urls })).IsValid);
    }

    [Fact]
    public void MoreThanSixImagesAreRejected()
    {
        var urls = Enumerable.Range(1, 7).Select(number => $"https://img.example.com/{number}.jpg").ToArray();

        var result = _validator.Validate(Request(ValidItem() with { ImageUrls = urls }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].ImageUrls");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//img.example.com/a.jpg")]
    [InlineData("ftp://img.example.com/a.jpg")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("https://")]
    [InlineData("https://user:secret@img.example.com/a.jpg")]
    [InlineData("https://img.example.com/a b.jpg")]
    public void ObviouslyInvalidImageUrlsAreRejected(string url)
    {
        var result = _validator.Validate(Request(ValidItem() with { ImageUrls = [url] }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].ImageUrls[0]");
    }

    [Fact]
    public void BrandWebsiteAndLogoAreOptional()
    {
        var item = ValidItem() with { Product = ValidProduct() with { BrandWebsiteUrl = "https://www.mavi.com", BrandLogoUrl = "https://cdn.mavi.com/logo.svg" } };

        Assert.True(_validator.Validate(Request(item)).IsValid);
    }

    [Theory]
    [InlineData("mavi.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void BrandWebsiteAndLogoMustBeAbsoluteHttpUrls(string url)
    {
        var item = ValidItem() with { Product = ValidProduct() with { BrandWebsiteUrl = url, BrandLogoUrl = url } };

        var result = _validator.Validate(Request(item));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Product.BrandWebsiteUrl");
        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Product.BrandLogoUrl");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ItemRequiresExternalId(string externalId)
    {
        var result = _validator.Validate(Request(ValidItem(externalId)));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].ExternalId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(199.999)]
    [InlineData(100000000000)]
    public void PriceMustBePositiveWithAtMostTwoDecimalsAndFitTheColumn(double price)
    {
        var result = _validator.Validate(Request(ValidItem() with { Price = (decimal)price }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Price");
    }

    [Theory]
    [InlineData("ftp://example.com/p/1")]
    [InlineData("/relative/path")]
    [InlineData("javascript:alert(1)")]
    public void UrlMustBeAbsoluteHttp(string url)
    {
        var result = _validator.Validate(Request(ValidItem() with { Url = url }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Url");
    }

    [Theory]
    [InlineData("instock")]
    [InlineData("LOWSTOCK")]
    public void EnumNamesAreCaseInsensitive(string stockStatus)
    {
        Assert.True(_validator.Validate(Request(ValidItem() with { StockStatus = stockStatus })).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("InStock,LowStock")]
    [InlineData("Backordered")]
    public void StockStatusMustBeASingleDefinedName(string stockStatus)
    {
        var result = _validator.Validate(Request(ValidItem() with { StockStatus = stockStatus }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].StockStatus");
    }

    [Theory]
    [InlineData("TL")]
    [InlineData("TR1")]
    [InlineData("TRYY")]
    public void CurrencyMustBeThreeLetters(string currency)
    {
        var result = _validator.Validate(Request(ValidItem() with { Currency = currency }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Currency");
    }

    [Fact]
    public void ProductNamesMustProduceASlug()
    {
        var item = ValidItem() with { Product = ValidProduct() with { Name = "!!!", BrandName = "---" } };

        var result = _validator.Validate(Request(item));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Product.Name");
        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Product.BrandName");
    }

    [Fact]
    public void MissingProductIsReported()
    {
        var result = _validator.Validate(Request(ValidItem() with { Product = null! }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[0].Product");
    }

    [Fact]
    public void NullItemIsReportedWithoutThrowing()
    {
        var result = _validator.Validate(new UpsertOffersRequest("trendyol", [ValidItem(), null!]));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items[1]");
    }

    [Fact]
    public void EmptyBatchIsRejected()
    {
        var result = _validator.Validate(Request());

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Items");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Trendyol")]
    [InlineData("two words")]
    public void SellerSlugMustBeKebabCase(string sellerSlug)
    {
        var result = _validator.Validate(RequestWith(1) with { SellerSlug = sellerSlug });

        Assert.Contains(result.Errors, failure => failure.PropertyName == "SellerSlug");
    }

    [Fact]
    public void BatchAtTheLimitIsValid()
    {
        Assert.True(_validator.Validate(RequestWith(UpsertLimits.MaxItems)).IsValid);
    }

    [Fact]
    public void OversizedBatchIsLeftToTheHandlerSoItIsNotValidatedItemByItem()
    {
        var items = Enumerable.Range(0, UpsertLimits.MaxItems + 1).Select(index => ValidItem($"ext-{index}") with { Price = -1m }).ToArray();

        var result = _validator.Validate(Request(items));

        Assert.True(result.IsValid);
    }
}
