using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Analytics.GetWeeklyMetrics;

/// <summary><c>From</c> is the earliest cohort week to return (a date); it defaults to twelve weeks back.</summary>
internal sealed record GetWeeklyMetricsQuery(DateOnly? From);

internal sealed record WeeklyMetricItem(DateOnly WeekStart, string Metric, decimal Value, long Numerator, long Denominator, DateTimeOffset ComputedAt);

internal sealed record WeeklyMetricsResponse(IReadOnlyList<WeeklyMetricItem> Items);

internal static class GetWeeklyMetricsEndpoint
{
    private const int DefaultWeeks = 12;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/weekly", Handle)
            .WithName("GetWeeklyMetrics")
            .Produces<WeeklyMetricsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>What the <c>weekly-metrics</c> job last computed, newest cohort week first. Values are percentages (0-100).</summary>
    private static async Task<IResult> Handle([AsParameters] GetWeeklyMetricsQuery query, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var from = query.From ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-7 * DefaultWeeks);

        var items = await db.WeeklyMetrics
            .AsNoTracking()
            .Where(row => row.WeekStart >= from)
            .OrderByDescending(row => row.WeekStart)
            .ThenBy(row => row.Metric)
            .Select(row => new WeeklyMetricItem(row.WeekStart, row.Metric, row.Value, row.Numerator, row.Denominator, row.ComputedAt))
            .ToListAsync(cancellationToken);

        return Results.Ok(new WeeklyMetricsResponse(items));
    }
}
