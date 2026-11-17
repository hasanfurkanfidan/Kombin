using System.Globalization;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;

namespace Kombin.Api.Features.League.Internal.CloseDay;

/// <summary>
/// <c>ClosedNow</c>: this call closed the day (false: it was closed before and nothing changed). The rest is the day's row: when it closed, the
/// ranked human outfits (the N of the percentile), the engine outfits, the counted votes in the fit, the Bradley–Terry iterations and whether
/// they converged, and how long the close took.
/// </summary>
internal sealed record CloseDayResponse(
    DateOnly Day, bool ClosedNow, DateTimeOffset ClosedAt, int RankedCount, int EngineCount, int PairCount, int BtIterations, bool BtConverged, int CloseMs);

internal static class CloseDayEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/days/{date}/close", Handle)
            .WithName("CloseLeagueDay")
            .Produces<CloseDayResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Closes one league day by hand (a day older than the hourly job's catch-up window, or an operator who will not wait for the next hour), with
    /// the same <see cref="DayCloser"/> as <c>league-close</c>. Closing a closed day changes nothing and answers 200 with <c>closedNow: false</c>.
    /// Errors: 400 <c>League.InvalidDay</c> (not <c>yyyy-MM-dd</c>); 409 <c>League.DayNotOver</c> (before the day's 21:00); 409
    /// <c>League.CloseInProgress</c> (another close holds the lock).
    /// </summary>
    private static async Task<IResult> Handle(string date, DayCloser closer, IClock clock, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return LeagueErrors.InvalidDay.ToHttpResult();
        }

        var result = await closer.CloseAsync(day, clock.UtcNow, cancellationToken);

        return result.Outcome switch
        {
            DayCloseOutcome.AlreadyRunning => LeagueErrors.CloseInProgress.ToHttpResult(),
            DayCloseOutcome.NotOver => LeagueErrors.DayNotOver.ToHttpResult(),
            _ => TypedResults.Ok(new CloseDayResponse(
                result.Day,
                result.Outcome == DayCloseOutcome.Closed,
                result.ClosedAt!.Value,
                result.RankedCount,
                result.EngineCount,
                result.PairCount,
                result.BtIterations,
                result.BtConverged,
                result.CloseMilliseconds)),
        };
    }
}
