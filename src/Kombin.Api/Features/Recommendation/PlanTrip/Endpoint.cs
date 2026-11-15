using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Recommendation.PlanTrip;

internal static class PlanTripEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/plan", Handle)
            .WithName("PlanTrip")
            .WithValidation<TripPlanRequest>()
            .RequireRateLimiting(RateLimitPolicies.Trips)
            .Produces<TripPlanResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// The smallest set of the caller's own pieces that makes an outfit for every day of the trip, with the weather of each day (forecast, climate
    /// normals or season), what each piece is worn for, and the gaps when some days cannot be dressed. Nothing is stored (<c>POST /me/trips</c> saves).
    /// </summary>
    private static async Task<IResult> Handle(
        TripPlanRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IWeatherProvider weather,
        IMemoryCache cache,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var plan = await TripPlanning.PlanAsync(db, weather, cache, userId, TripAsk.From(request), clock.UtcNow, cancellationToken);
        if (plan.IsFailure)
        {
            return plan.Error.ToHttpResult();
        }

        return TypedResults.Ok(await TripPlanning.RespondAsync(db, storage, userId, plan.Value, cancellationToken));
    }
}
