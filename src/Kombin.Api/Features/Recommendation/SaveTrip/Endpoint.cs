using System.Text.Json;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Recommendation.SaveTrip;

/// <summary><c>Name</c> (1-60 characters) and the plan request (<c>POST /me/trips/plan</c>'s body).</summary>
internal sealed record SaveTripRequest(string? Name, TripPlanRequest? Request);

/// <summary>A saved trip: its name, what was asked, when it was last planned, and the plan.</summary>
internal sealed record TripResponse(Guid Id, string Name, TripPlanRequest Request, DateTimeOffset PlannedAt, DateTimeOffset CreatedAt, TripPlanResponse Plan);

internal static class SaveTripEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/", Handle)
            .WithName("SaveTrip")
            .WithValidation<SaveTripRequest>()
            .RequireRateLimiting(RateLimitPolicies.Trips)
            .Produces<TripResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Plans the trip again on the server and stores that plan with the request (a client's copy of a plan is never stored), so the saved plan is
    /// exactly what the server would answer now. At most 50 trips per user.
    /// </summary>
    private static async Task<IResult> Handle(
        SaveTripRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IWeatherProvider weather,
        IMemoryCache cache,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (await db.Trips.CountAsync(trip => trip.UserId == userId, cancellationToken) >= TripPlanning.MaxSavedTrips)
        {
            return RecommendationErrors.TooManyTrips.ToHttpResult();
        }

        var now = clock.UtcNow;
        var plan = await TripPlanning.PlanAsync(db, weather, cache, userId, TripAsk.From(request.Request!), now, cancellationToken);
        if (plan.IsFailure)
        {
            return plan.Error.ToHttpResult();
        }

        var trip = new Trip
        {
            UserId = userId,
            Name = request.Name!.Trim(),
            City = plan.Value.City,
            StartDate = request.Request!.StartDate!.Value,
            EndDate = request.Request.EndDate!.Value,
            RequestJson = JsonSerializer.Serialize(request.Request, TripPlanning.Json),
        };
        TripStore.Apply(trip, plan.Value, now);
        db.Trips.Add(trip);
        await db.SaveChangesAsync(cancellationToken);

        var response = await TripStore.RespondAsync(db, storage, trip, cancellationToken);
        return TypedResults.Created($"/api/v1/me/trips/{trip.Id}", response);
    }
}
