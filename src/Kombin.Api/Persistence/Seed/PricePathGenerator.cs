using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Persistence.Seed;

internal readonly record struct HistoryPoint(DateTimeOffset ObservedAt, decimal Price, decimal? OriginalPrice, StockStatus StockStatus);

/// <summary>
/// Builds a plausible 90-day price history for one offer, ending at its current price: it starts 3-25 % above, drops through
/// 2-5 step changes, has one or two short dips and sometimes a short stock-out. Pure and seeded, so the same inputs always give
/// the same series.
/// </summary>
internal static class PricePathGenerator
{
    public const int WindowDays = 90;

    /// <summary>The last samples are kept free of random dips and stock-outs, so the series ends cleanly on the current price.</summary>
    private const int TailSamples = 3;

    /// <summary>
    /// One sample every 1-4 days from the start of the 90-day window up to yesterday (today's observation comes from the upsert).
    /// </summary>
    /// <param name="seed">Seeds the random source.</param>
    /// <param name="now">Today; samples are placed on the days before it.</param>
    /// <param name="currentPrice">The price the path ends at. Must be positive.</param>
    /// <param name="currentOriginalPrice">Carried by samples whose price equals <paramref name="currentPrice"/>.</param>
    /// <param name="currentStock">An out-of-stock offer ends with two out-of-stock samples, a low-stock one with one.</param>
    /// <param name="floor">When set, no sample is cheaper than this (keeps the product at its 90-day low).</param>
    /// <param name="dipBelowCurrent">Forces one in-stock sample at least 8 % below <paramref name="currentPrice"/> (so the product is not at its 90-day low).</param>
    public static IReadOnlyList<HistoryPoint> Generate(
        int seed,
        DateTimeOffset now,
        decimal currentPrice,
        decimal? currentOriginalPrice,
        StockStatus currentStock,
        decimal? floor = null,
        bool dipBelowCurrent = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(currentPrice, 0m);

        if (floor is not null && dipBelowCurrent)
        {
            throw new ArgumentException("A floor and a forced dip below the current price contradict each other.", nameof(dipBelowCurrent));
        }

        var random = new Random(seed);
        var today = now.UtcDateTime.Date;
        var offsets = SampleOffsets(random);
        var count = offsets.Count;
        var lastDippable = count - TailSamples - 1;

        var prices = StepPath(random, currentPrice, count);
        var baseline = (decimal[])prices.Clone();

        for (var dips = random.Next(1, 3); dips > 0; dips--)
        {
            var depth = (decimal)(0.85 + random.NextDouble() * 0.10);
            var at = random.Next(1, lastDippable + 1);

            foreach (var index in Enumerable.Range(at, random.Next(1, 3)).Where(index => index <= lastDippable))
            {
                prices[index] = Retail(baseline[index] * depth, currentPrice);
            }
        }

        var stocks = new StockStatus[count];

        if (random.NextDouble() < 0.6)
        {
            var at = random.Next(1, lastDippable + 1);

            foreach (var index in Enumerable.Range(at, random.Next(1, 3)).Where(index => index <= lastDippable))
            {
                stocks[index] = StockStatus.OutOfStock;
            }
        }

        if (currentStock == StockStatus.OutOfStock)
        {
            stocks[count - 2] = StockStatus.OutOfStock;
            stocks[count - 1] = StockStatus.OutOfStock;
        }
        else if (currentStock == StockStatus.LowStock)
        {
            stocks[count - 1] = StockStatus.LowStock;
        }

        if (dipBelowCurrent)
        {
            var candidates = Enumerable.Range(1, lastDippable).Where(index => stocks[index] == StockStatus.InStock).ToList();
            var dipped = Retail(currentPrice * (decimal)(0.80 + random.NextDouble() * 0.12), currentPrice);
            prices[candidates[random.Next(candidates.Count)]] = dipped < currentPrice ? dipped : Math.Round(currentPrice * 0.9m, 2);
        }

        var points = new List<HistoryPoint>(count);

        for (var index = 0; index < count; index++)
        {
            var price = Math.Max(floor is { } minimum ? Math.Max(prices[index], minimum) : prices[index], 0.01m);
            var observedAt = today.AddDays(offsets[index]).AddMinutes(random.Next(0, 600));

            points.Add(new HistoryPoint(
                new DateTimeOffset(observedAt, TimeSpan.Zero),
                price,
                price == currentPrice ? currentOriginalPrice : null,
                stocks[index]));
        }

        return points;
    }

    /// <summary>Day offsets from today (negative), 1-4 days apart, from the window start to yesterday inclusive.</summary>
    private static List<int> SampleOffsets(Random random)
    {
        var offsets = new List<int>();

        for (var offset = -(WindowDays - 1) + random.Next(0, 2); offset <= -1; offset += random.Next(1, 5))
        {
            offsets.Add(offset);
        }

        if (offsets[^1] != -1)
        {
            offsets.Add(-1);
        }

        return offsets;
    }

    /// <summary>A step function from a starting level 3-25 % above the current price down to it (never below it), with 2-5 change points.</summary>
    private static decimal[] StepPath(Random random, decimal currentPrice, int count)
    {
        var steps = random.Next(2, 6);
        var changeAt = Enumerable.Range(1, count - 1).OrderBy(_ => random.Next()).Take(steps).Order().ToList();

        var start = currentPrice * (decimal)(1.03 + random.NextDouble() * 0.22);
        var levels = new decimal[steps + 1];
        levels[0] = Retail(start, currentPrice);

        for (var step = 1; step < steps; step++)
        {
            var interpolated = start + (currentPrice - start) * step / steps;
            levels[step] = Math.Max(Retail(interpolated * (decimal)(1 + (random.NextDouble() - 0.5) * 0.06), currentPrice), currentPrice);
        }

        levels[steps] = currentPrice;

        var prices = new decimal[count];
        var level = 0;

        for (var index = 0; index < count; index++)
        {
            while (level < steps && index >= changeAt[level])
            {
                level++;
            }

            prices[index] = levels[level];
        }

        return prices;
    }

    /// <summary>Rounds to the nearest 10 TL while keeping the reference price's ending (449.90 stays x9.90); cheap prices just round to kuruş.</summary>
    private static decimal Retail(decimal value, decimal reference)
    {
        if (reference < 100m)
        {
            return Math.Max(Math.Round(value, 2), 0.01m);
        }

        var ending = reference % 10m;
        var rounded = Math.Round((value - ending) / 10m, MidpointRounding.AwayFromZero) * 10m + ending;

        return Math.Max(rounded, 0.01m);
    }
}
