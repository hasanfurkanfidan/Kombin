using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Tests;

public class SnapshotRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static PriceSnapshot Last(
        decimal price = 100m, decimal? originalPrice = 150m, StockStatus stockStatus = StockStatus.InStock, TimeSpan? age = null) =>
        new() { Price = price, OriginalPrice = originalPrice, StockStatus = stockStatus, ObservedAt = Now - (age ?? TimeSpan.FromHours(1)) };

    [Fact]
    public void FirstObservationIsAlwaysWritten()
    {
        Assert.True(SnapshotRule.ShouldWrite(null, 100m, null, StockStatus.InStock, Now));
    }

    [Fact]
    public void UnchangedRecentObservationIsNotWritten()
    {
        Assert.False(SnapshotRule.ShouldWrite(Last(), 100m, 150m, StockStatus.InStock, Now));
    }

    [Fact]
    public void ChangedPriceIsWritten()
    {
        Assert.True(SnapshotRule.ShouldWrite(Last(), 99.90m, 150m, StockStatus.InStock, Now));
    }

    [Theory]
    [InlineData(200.0)]
    [InlineData(null)]
    public void ChangedOriginalPriceIsWritten(double? originalPrice)
    {
        Assert.True(SnapshotRule.ShouldWrite(Last(), 100m, (decimal?)originalPrice, StockStatus.InStock, Now));
    }

    [Fact]
    public void ChangedStockStatusIsWritten()
    {
        Assert.True(SnapshotRule.ShouldWrite(Last(), 100m, 150m, StockStatus.OutOfStock, Now));
    }

    [Fact]
    public void UnchangedObservationOlderThanADayIsWritten()
    {
        var last = Last(age: TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        Assert.True(SnapshotRule.ShouldWrite(last, 100m, 150m, StockStatus.InStock, Now));
    }

    [Fact]
    public void UnchangedObservationJustUnderADayOldIsNotWritten()
    {
        var last = Last(age: TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));

        Assert.False(SnapshotRule.ShouldWrite(last, 100m, 150m, StockStatus.InStock, Now));
    }

    [Fact]
    public void TrailingZerosDoNotCountAsAChange()
    {
        Assert.False(SnapshotRule.ShouldWrite(Last(price: 100m), 100.00m, 150.0m, StockStatus.InStock, Now));
    }
}
