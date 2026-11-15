using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Recommendation.SaveTrip;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>Reading and writing <see cref="Trip"/> rows: the plan snapshot and its counts, and the saved trip's answer.</summary>
internal static class TripStore
{
    public static void Apply(Trip trip, TripPlanSnapshot plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(plan);

        trip.PlanJson = JsonSerializer.Serialize(plan, TripPlanning.Json);
        trip.PieceCount = plan.PieceCount;
        trip.CoveredDays = plan.Covered;
        trip.TotalDays = plan.Days.Count;
        trip.PlannedAt = now;
    }

    public static TripPlanRequest Request(Trip trip) => JsonSerializer.Deserialize<TripPlanRequest>(trip.RequestJson, TripPlanning.Json)!;

    public static TripPlanSnapshot Plan(Trip trip) => JsonSerializer.Deserialize<TripPlanSnapshot>(trip.PlanJson, TripPlanning.Json)!;

    public static async Task<TripResponse> RespondAsync(KombinDbContext db, IObjectStorage storage, Trip trip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trip);

        var plan = await TripPlanning.RespondAsync(db, storage, trip.UserId, Plan(trip), cancellationToken);

        return new TripResponse(trip.Id, trip.Name, Request(trip), trip.PlannedAt, trip.CreatedAt, plan);
    }
}
