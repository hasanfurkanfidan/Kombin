using Kombin.Api.Common.Time;
using Kombin.Api.Features.Engagement;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kombin.Api.Tests;

public class AlertEvaluatorTests
{
    private static Offer MakeOffer(
        decimal price,
        StockStatus stock = StockStatus.InStock,
        decimal? shippingCost = null,
        decimal? freeShippingThreshold = null,
        params string[] sizes) =>
        new()
        {
            CurrentPrice = price,
            StockStatus = stock,
            ShippingCost = shippingCost,
            FreeShippingThreshold = freeShippingThreshold,
            AvailableSizes = sizes,
        };

    // Theory data cannot carry the internal enum through a public test method, so theories pass its name.
    private static StockStatus Stock(string name) => Enum.Parse<StockStatus>(name);

    private static PriceAlert PriceDrop(decimal target) => new() { Kind = AlertKind.PriceDrop, TargetPrice = target };

    private static PriceAlert BackInStock(params string[] sizes) => new() { Kind = AlertKind.BackInStock, Sizes = sizes };

    private static PriceAlert AnyChange() => new() { Kind = AlertKind.AnyChange };

    private static AlertHit? Evaluate(PriceAlert alert, Offer offer, decimal? previousPrice, StockStatus previousStock = StockStatus.InStock) =>
        AlertEvaluator.Evaluate(alert, offer, previousPrice, previousStock);

    [Fact]
    public void PriceDropFiresWhenThePriceCrossesTheTargetFromAbove()
    {
        var hit = Evaluate(PriceDrop(100m), MakeOffer(90m), previousPrice: 120m);

        Assert.NotNull(hit);
        Assert.Equal(120m, hit.PriceBefore);
        Assert.Equal(90m, hit.PriceAfter);
    }

    [Fact]
    public void PriceDropFiresWhenThePriceLandsExactlyOnTheTarget()
    {
        Assert.NotNull(Evaluate(PriceDrop(100m), MakeOffer(100m), previousPrice: 120m));
    }

    [Fact]
    public void PriceDropDoesNotFireAgainWhileThePriceStaysBelowTheTarget()
    {
        Assert.Null(Evaluate(PriceDrop(100m), MakeOffer(85m), previousPrice: 90m));
        Assert.Null(Evaluate(PriceDrop(100m), MakeOffer(90m), previousPrice: 90m));
    }

    [Fact]
    public void PriceDropFiresAgainAfterThePriceWentBackUpAndDroppedAgain()
    {
        Assert.Null(Evaluate(PriceDrop(100m), MakeOffer(110m), previousPrice: 90m));
        Assert.NotNull(Evaluate(PriceDrop(100m), MakeOffer(95m), previousPrice: 110m));
    }

    [Fact]
    public void PriceDropDoesNotFireWhileThePriceIsStillAboveTheTarget()
    {
        Assert.Null(Evaluate(PriceDrop(100m), MakeOffer(110m), previousPrice: 120m));
    }

    [Fact]
    public void PriceDropComparesWithThePriceIncludingShipping()
    {
        // 90 + 20 shipping = 110: not yet below 100.
        Assert.Null(Evaluate(PriceDrop(100m), MakeOffer(90m, shippingCost: 20m), previousPrice: 130m));

        // The same price reaching the free-shipping threshold is effectively 90.
        Assert.NotNull(Evaluate(PriceDrop(100m), MakeOffer(90m, shippingCost: 20m, freeShippingThreshold: 90m), previousPrice: 130m));
    }

    [Fact]
    public void PriceDropIgnoresAnOfferThatCanNotBeBought()
    {
        Assert.Null(Evaluate(PriceDrop(100m), MakeOffer(90m, StockStatus.OutOfStock), previousPrice: 120m, StockStatus.OutOfStock));
    }

    [Theory]
    [InlineData("OutOfStock")]
    [InlineData("Unknown")]
    public void PriceDropFiresWhenAnOfferComesBackAtAPriceUnderTheTarget(string previousStock)
    {
        // A price seen while it could not be bought was never a price the user could have used.
        Assert.NotNull(Evaluate(PriceDrop(100m), MakeOffer(90m), previousPrice: 90m, Stock(previousStock)));
    }

    [Fact]
    public void NothingFiresForAnOfferThatWasNeverObservedBefore()
    {
        Assert.Null(Evaluate(PriceDrop(100m), MakeOffer(90m), previousPrice: null));
        Assert.Null(Evaluate(BackInStock(), MakeOffer(90m), previousPrice: null, StockStatus.OutOfStock));
        Assert.Null(Evaluate(AnyChange(), MakeOffer(90m), previousPrice: null, StockStatus.OutOfStock));
    }

    [Theory]
    [InlineData("OutOfStock", "InStock")]
    [InlineData("OutOfStock", "LowStock")]
    [InlineData("Unknown", "InStock")]
    [InlineData("Unknown", "LowStock")]
    public void BackInStockFiresWhenStockReturns(string previous, string current)
    {
        var hit = Evaluate(BackInStock(), MakeOffer(100m, Stock(current), sizes: ["S", "M"]), previousPrice: 100m, Stock(previous));

        Assert.NotNull(hit);
        Assert.Equal(["S", "M"], hit.Sizes);
    }

    [Theory]
    [InlineData("InStock", "LowStock")]
    [InlineData("LowStock", "InStock")]
    [InlineData("InStock", "OutOfStock")]
    [InlineData("OutOfStock", "OutOfStock")]
    [InlineData("OutOfStock", "Unknown")]
    public void BackInStockIgnoresEveryOtherStockMovement(string previous, string current)
    {
        Assert.Null(Evaluate(BackInStock(), MakeOffer(100m, Stock(current), sizes: ["S"]), previousPrice: 100m, Stock(previous)));
    }

    [Fact]
    public void BackInStockWithSizesFiresOnlyWhenOneOfThemIsAvailable()
    {
        var offer = MakeOffer(100m, StockStatus.InStock, sizes: ["S", "M", "38"]);

        var hit = Evaluate(BackInStock("M", "XL"), offer, previousPrice: 100m, StockStatus.OutOfStock);

        Assert.NotNull(hit);
        Assert.Equal(["M"], hit.Sizes);
        Assert.Null(Evaluate(BackInStock("XL", "XXL"), offer, previousPrice: 100m, StockStatus.OutOfStock));
    }

    [Fact]
    public void BackInStockSizesAreMatchedIgnoringCase()
    {
        var offer = MakeOffer(100m, StockStatus.InStock, sizes: ["xl"]);

        Assert.NotNull(Evaluate(BackInStock("XL"), offer, previousPrice: 100m, StockStatus.OutOfStock));
    }

    [Theory]
    [InlineData(100, 101, true)]
    [InlineData(100, 99, true)]
    [InlineData(100, 100.99, false)]
    [InlineData(100, 99.01, false)]
    [InlineData(100, 100, false)]
    [InlineData(100, 150, true)]
    public void AnyChangeNeedsAPriceMoveOfAtLeastOnePercent(double previous, double current, bool expected)
    {
        var hit = Evaluate(AnyChange(), MakeOffer((decimal)current), previousPrice: (decimal)previous);

        Assert.Equal(expected, hit is not null);
    }

    [Theory]
    [InlineData("InStock", "LowStock")]
    [InlineData("InStock", "OutOfStock")]
    [InlineData("OutOfStock", "InStock")]
    [InlineData("Unknown", "OutOfStock")]
    public void AnyChangeFiresOnAnyStockStatusChange(string previous, string current)
    {
        Assert.NotNull(Evaluate(AnyChange(), MakeOffer(100m, Stock(current)), previousPrice: 100m, Stock(previous)));
    }

    [Fact]
    public void AnyChangeDoesNotFireOnAnUnchangedObservation()
    {
        Assert.Null(Evaluate(AnyChange(), MakeOffer(100m), previousPrice: 100m));
    }

    [Fact]
    public void AnyChangeUsesThePriceIncludingShipping()
    {
        // 100 -> 100 sticker price, but shipping went from 0 to 5: effective 100 -> 105.
        Assert.NotNull(Evaluate(AnyChange(), MakeOffer(100m, shippingCost: 5m), previousPrice: 100m));
    }

    [Fact]
    public async Task AFailingEvaluationIsLoggedWithTheOfferIdAndDoesNotThrow()
    {
        // Nothing listens on port 1, so the first query fails like any database outage would.
        var options = new DbContextOptionsBuilder<KombinDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Command Timeout=1;Pooling=false", npgsql => npgsql.UseVector())
            .Options;
        await using var db = new KombinDbContext(options);
        var logger = new CapturingLogger();
        var evaluator = new AlertEvaluator(db, new FixedClock(DateTimeOffset.UnixEpoch), logger);
        var offer = MakeOffer(90m);

        await evaluator.EvaluateAsync(offer, previousPrice: 120m, StockStatus.InStock, EvaluationReason.Observation, TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(offer.Id.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }

    private sealed class CapturingLogger : ILogger<AlertEvaluator>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
