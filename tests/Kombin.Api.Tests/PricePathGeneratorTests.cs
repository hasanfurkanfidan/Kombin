using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Seed;

namespace Kombin.Api.Tests;

public class PricePathGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 14, 30, 0, TimeSpan.Zero);

    private static IReadOnlyList<HistoryPoint> Generate(
        int seed = 1, decimal price = 449.90m, decimal? original = null, StockStatus stock = StockStatus.InStock, decimal? floor = null, bool dip = false) =>
        PricePathGenerator.Generate(seed, Now, price, original, stock, floor, dip);

    [Fact]
    public void TheSameSeedGivesTheSameSeries()
    {
        Assert.Equal(Generate(seed: 42, original: 599.90m), Generate(seed: 42, original: 599.90m));
    }

    [Fact]
    public void DifferentSeedsGiveDifferentSeries()
    {
        Assert.NotEqual(Generate(seed: 1), Generate(seed: 2));
    }

    [Fact]
    public void StableSeedDoesNotDependOnTheProcess()
    {
        Assert.Equal(DevSeeder.StableSeed("trendyol", "trendyol-mv-001"), DevSeeder.StableSeed("trendyol", "trendyol-mv-001"));
        Assert.NotEqual(DevSeeder.StableSeed("trendyol", "trendyol-mv-001"), DevSeeder.StableSeed("hepsiburada", "trendyol-mv-001"));
        Assert.True(DevSeeder.StableSeed("trendyol", "x") >= 0);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(1.00)]
    [InlineData(9.99)]
    [InlineData(99.90)]
    [InlineData(100.00)]
    [InlineData(299.99)]
    [InlineData(449.90)]
    [InlineData(2499.00)]
    [InlineData(6990.00)]
    [InlineData(250000.00)]
    public void PricesArePositiveWithAtMostTwoDecimals(double value)
    {
        var price = (decimal)value;

        for (var seed = 0; seed < 200; seed++)
        {
            foreach (var point in Generate(seed, price))
            {
                Assert.True(point.Price > 0m, $"seed {seed}: {point.Price}");
                Assert.Equal(point.Price, Math.Round(point.Price, 2));
            }
        }
    }

    [Fact]
    public void SamplesAreOneToFourDaysApartAndCoverTheWindowUpToYesterday()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var points = Generate(seed);
            var days = points.Select(point => DateOnly.FromDateTime(point.ObservedAt.UtcDateTime)).ToList();

            Assert.Equal(new DateOnly(2026, 10, 31), days[^1]);
            var windowStart = DateOnly.FromDateTime(Now.WindowStart(PricePathGenerator.WindowDays).UtcDateTime);
            Assert.InRange(days[0], windowStart, windowStart.AddDays(1));
            Assert.All(days.Zip(days.Skip(1)), pair => Assert.InRange(pair.Second.DayNumber - pair.First.DayNumber, 1, 4));
            Assert.Equal(points.OrderBy(point => point.ObservedAt).ToList(), points);
        }
    }

    [Fact]
    public void ThePathStartsAboveAndEndsOnTheCurrentPrice()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var points = Generate(seed, 1499.90m, original: 1899.90m);

            Assert.InRange(points[0].Price, 1499.90m, 1499.90m * 1.30m);
            Assert.Equal(1499.90m, points[^1].Price);
            Assert.Equal(1899.90m, points[^1].OriginalPrice);
            Assert.All(points.Where(point => point.Price != 1499.90m), point => Assert.Null(point.OriginalPrice));
        }
    }

    [Fact]
    public void ThePathMovesThroughSeveralLevels()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var distinct = Generate(seed).Select(point => point.Price).Distinct().Count();

            Assert.InRange(distinct, 3, 10);
        }
    }

    [Fact]
    public void AFloorIsNeverUndercut()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            Assert.All(Generate(seed, 449.90m, floor: 449.90m), point => Assert.True(point.Price >= 449.90m));
        }
    }

    [Fact]
    public void ADipBelowTheCurrentPriceHappensOnAnInStockSample()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var points = Generate(seed, 449.90m, dip: true);

            Assert.Contains(points, point => point.StockStatus == StockStatus.InStock && point.Price <= 449.90m * 0.92m + 5m && point.Price < 449.90m);
        }
    }

    [Fact]
    public void WithoutAnyFlagThePathNeverGoesMuchBelowTheCurrentPrice()
    {
        // Dips are at most 15 % off a level that is itself at or above the current price.
        for (var seed = 0; seed < 100; seed++)
        {
            Assert.All(Generate(seed, 1000m), point => Assert.True(point.Price >= 1000m * 0.80m));
        }
    }

    [Fact]
    public void AFloorAndAForcedDipContradict()
    {
        Assert.Throws<ArgumentException>(() => Generate(floor: 100m, dip: true));
    }

    [Fact]
    public void ACurrentPriceMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Generate(price: 0m));
    }

    [Fact]
    public void StockStatusEndsWhereTheOfferIs()
    {
        Assert.Equal(StockStatus.OutOfStock, Generate(stock: StockStatus.OutOfStock)[^1].StockStatus);
        Assert.Equal(StockStatus.OutOfStock, Generate(stock: StockStatus.OutOfStock)[^2].StockStatus);
        Assert.Equal(StockStatus.LowStock, Generate(stock: StockStatus.LowStock)[^1].StockStatus);
        Assert.Equal(StockStatus.InStock, Generate(stock: StockStatus.InStock)[^1].StockStatus);
    }

    [Fact]
    public void SomeSeriesContainAShortStockOut()
    {
        var withStockOut = Enumerable.Range(0, 100).Count(seed => Generate(seed).Any(point => point.StockStatus == StockStatus.OutOfStock));

        Assert.InRange(withStockOut, 30, 90);
    }
}

