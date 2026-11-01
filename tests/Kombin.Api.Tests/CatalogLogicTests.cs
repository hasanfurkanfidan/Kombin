using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Features.Catalog.ListProducts;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Tests;

public class CatalogLogicTests
{
    [Theory]
    [InlineData(100.0, null, null, 100.0)]
    [InlineData(100.0, 49.9, null, 149.9)]
    [InlineData(100.0, 49.9, 100.0, 100.0)]
    [InlineData(100.0, 49.9, 150.0, 149.9)]
    [InlineData(100.0, 0.0, null, 100.0)]
    public void EffectivePriceAddsShippingUnlessTheThresholdIsReached(double price, double? shipping, double? threshold, double expected)
    {
        var effective = GetProductEndpoint.EffectivePrice((decimal)price, (decimal?)shipping, (decimal?)threshold);

        Assert.Equal((decimal)expected, effective);
    }

    [Theory]
    [InlineData(150.0, 200.0, 25)]
    [InlineData(150.0, 150.0, 0)]
    [InlineData(150.0, 100.0, 0)]
    [InlineData(150.0, null, 0)]
    [InlineData(199.9, 299.9, 33)]
    public void DiscountPercentComparesTheLowestOffersOriginalAndCurrentPrice(double lowest, double? original, int expected)
    {
        Assert.Equal(expected, ListProductsEndpoint.DiscountPercent((decimal)lowest, (decimal?)original));
    }

    [Fact]
    public void WindowStartCoversTheRequestedCalendarDaysIncludingToday()
    {
        var now = new DateTimeOffset(2026, 11, 10, 15, 30, 0, TimeSpan.FromHours(3));

        var start = now.WindowStart(7);

        Assert.Equal(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void ParseRejectsNumbersAndLists()
    {
        Assert.True(EnumNames.TryParse<StockStatus>("lowstock", out var parsed));
        Assert.Equal(StockStatus.LowStock, parsed);
        Assert.False(EnumNames.TryParse<StockStatus>("1", out _));
        Assert.False(EnumNames.TryParse<StockStatus>("InStock, LowStock", out _));
        Assert.False(EnumNames.TryParse<StockStatus>(null, out _));
    }

    [Fact]
    public void SeasonsRoundTripThroughNames()
    {
        var seasons = EnumNames.ParseSeasons(["winter", "Autumn"]);

        Assert.Equal(Season.Autumn | Season.Winter, seasons);
        Assert.Equal(["Autumn", "Winter"], EnumNames.SeasonNames(seasons));
        Assert.Equal([], EnumNames.SeasonNames(Season.None));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("AllYear")]
    [InlineData("Monsoon")]
    public void SeasonValidationOnlyAcceptsTheFourSeasons(string name)
    {
        Assert.False(EnumNames.IsValidSeason(name));
        Assert.True(EnumNames.IsValidSeason("spring"));
    }
}
