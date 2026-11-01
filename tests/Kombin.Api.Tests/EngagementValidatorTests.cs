using Kombin.Api.Features.Engagement;
using Kombin.Api.Features.Engagement.CreateAlert;
using Kombin.Api.Features.Engagement.UpdateAlert;

namespace Kombin.Api.Tests;

public class EngagementValidatorTests
{
    private readonly CreateAlertValidator _create = new();

    private readonly UpdateAlertValidator _update = new();

    private static CreateAlertRequest PriceDrop(decimal? target = 99.90m) => new(Guid.NewGuid(), "PriceDrop", target, null);

    [Fact]
    public void AValidPriceDropPasses()
    {
        Assert.True(_create.Validate(PriceDrop()).IsValid);
    }

    [Theory]
    [InlineData("BackInStock")]
    [InlineData("anychange")]
    public void AlertsWithoutATargetPass(string kind)
    {
        Assert.True(_create.Validate(new CreateAlertRequest(Guid.NewGuid(), kind, null, null)).IsValid);
    }

    [Fact]
    public void BackInStockMayNameSizes()
    {
        Assert.True(_create.Validate(new CreateAlertRequest(Guid.NewGuid(), "BackInStock", null, ["S", "38"])).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Cheaper")]
    [InlineData("1")]
    [InlineData("PriceDrop,AnyChange")]
    public void KindMustBeASingleDefinedName(string kind)
    {
        var result = _create.Validate(new CreateAlertRequest(Guid.NewGuid(), kind, 10m, null));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Kind");
    }

    [Fact]
    public void PriceDropRequiresATarget()
    {
        var result = _create.Validate(PriceDrop(target: null));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "TargetPrice");
    }

    [Theory]
    [InlineData("BackInStock")]
    [InlineData("AnyChange")]
    public void OtherKindsForbidATarget(string kind)
    {
        var result = _create.Validate(new CreateAlertRequest(Guid.NewGuid(), kind, 50m, null));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "TargetPrice");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.999)]
    [InlineData(100000000000)]
    public void TheTargetMustBePositiveWithAtMostTwoDecimalsAndFitTheColumn(double target)
    {
        var result = _create.Validate(PriceDrop((decimal)target));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "TargetPrice");
    }

    [Theory]
    [InlineData("PriceDrop")]
    [InlineData("AnyChange")]
    public void OnlyBackInStockMayNameSizes(string kind)
    {
        var request = new CreateAlertRequest(Guid.NewGuid(), kind, kind == "PriceDrop" ? 10m : null, ["S"]);

        var result = _create.Validate(request);

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Sizes");
    }

    [Fact]
    public void SizesAreCheckedEntryByEntry()
    {
        var result = _create.Validate(new CreateAlertRequest(Guid.NewGuid(), "BackInStock", null, ["S", "", new string('x', 21)]));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Sizes[1]");
        Assert.Contains(result.Errors, failure => failure.PropertyName == "Sizes[2]");
    }

    [Fact]
    public void TooManySizesAreRejected()
    {
        var sizes = Enumerable.Range(0, 31).Select(index => $"{30 + index}").ToArray();

        var result = _create.Validate(new CreateAlertRequest(Guid.NewGuid(), "BackInStock", null, sizes));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Sizes");
    }

    [Fact]
    public void AnEmptyProductIdIsRejected()
    {
        var result = _create.Validate(PriceDrop() with { ProductId = Guid.Empty });

        Assert.Contains(result.Errors, failure => failure.PropertyName == "ProductId");
    }

    [Theory]
    [InlineData(25.5, null, null)]
    [InlineData(null, new[] { "S" }, null)]
    [InlineData(null, new string[0], null)]
    [InlineData(null, null, false)]
    public void AnUpdateWithAtLeastOneFieldPasses(double? target, string[]? sizes, bool? isActive)
    {
        var request = new UpdateAlertRequest(target is { } value ? (decimal)value : null, sizes, isActive);

        Assert.True(_update.Validate(request).IsValid);
    }

    [Fact]
    public void AnEmptyUpdateIsRejected()
    {
        var result = _update.Validate(new UpdateAlertRequest(null, null, null));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "body");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1.005)]
    public void AnUpdatedTargetFollowsTheSameRules(double target)
    {
        var result = _update.Validate(new UpdateAlertRequest((decimal)target, null, null));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "TargetPrice");
    }

    [Fact]
    public void UpdatedSizesAreCheckedEntryByEntry()
    {
        var result = _update.Validate(new UpdateAlertRequest(null, ["M", " "], null));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Sizes[1]");
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("short", false)]
    [InlineData("", false)]
    public void TheIpSaltMustBeLongEnough(string salt, bool valid)
    {
        var validator = new OutboundOptionsValidator(new TestEnvironment("Production"));

        Assert.Equal(valid, validator.Validate(null, new OutboundOptions { IpSalt = salt }).Succeeded);
    }

    [Fact]
    public void ThePlaceholderSaltIsOnlyAcceptedInDevelopment()
    {
        var options = new OutboundOptions { IpSalt = "dev-only-ip-salt-change-me-0123456789abcdef" };

        Assert.True(new OutboundOptionsValidator(new TestEnvironment("Development")).Validate(null, options).Succeeded);
        Assert.False(new OutboundOptionsValidator(new TestEnvironment("Production")).Validate(null, options).Succeeded);
    }

    private sealed class TestEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Kombin.Api.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
