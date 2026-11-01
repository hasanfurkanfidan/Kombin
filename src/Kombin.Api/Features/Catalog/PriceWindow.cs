namespace Kombin.Api.Features.Catalog;

internal static class PriceWindow
{
    /// <summary>Start (UTC midnight) of a window covering <paramref name="days"/> calendar days, today included.</summary>
    public static DateTimeOffset WindowStart(this DateTimeOffset now, int days) =>
        new(now.UtcDateTime.Date.AddDays(-(days - 1)), TimeSpan.Zero);
}
