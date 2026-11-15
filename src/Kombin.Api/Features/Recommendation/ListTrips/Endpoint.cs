using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Recommendation.ListTrips;

internal sealed record TripSummaryResponse(
    Guid Id, string Name, string City, DateOnly StartDate, DateOnly EndDate, int PieceCount, TripCoverage Coverage, DateTimeOffset PlannedAt, DateTimeOffset CreatedAt);

internal sealed record TripListResponse(IReadOnlyList<TripSummaryResponse> Items);

internal static class ListTripsEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListTrips")
            .Produces<TripListResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>The caller's saved trips, newest first (at most 50, so no paging).</summary>
    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var trips = await db.Trips.AsNoTracking()
            .Where(trip => trip.UserId == userId)
            .OrderByDescending(trip => trip.CreatedAt)
            .ThenByDescending(trip => trip.Id)
            .Select(trip => new { trip.Id, trip.Name, trip.City, trip.StartDate, trip.EndDate, trip.PieceCount, trip.CoveredDays, trip.TotalDays, trip.PlannedAt, trip.CreatedAt })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new TripListResponse(
        [
            .. trips.Select(trip => new TripSummaryResponse(
                trip.Id,
                trip.Name,
                trip.City,
                trip.StartDate,
                trip.EndDate,
                trip.PieceCount,
                new TripCoverage(trip.CoveredDays, trip.TotalDays, trip.TotalDays == 0 ? 0 : (int)Math.Round(100.0 * trip.CoveredDays / trip.TotalDays, MidpointRounding.AwayFromZero)),
                trip.PlannedAt,
                trip.CreatedAt)),
        ]));
    }
}
