using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation.SaveTrip;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Recommendation.ReplanTrip;

internal static class ReplanTripEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/replan", Handle)
            .WithName("ReplanTrip")
            .RequireRateLimiting(RateLimitPolicies.Trips)
            .Produces<TripResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Plans a saved trip again with today's weather and wardrobe and stores the new plan. A trip already under way is planned from today; one that
    /// is over is 409 <c>Recommendation.TripOver</c>. If the persona it was planned for is gone, the default persona is used. Someone else's trip is 404.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IWeatherProvider weather,
        IMemoryCache cache,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var trip = await db.Trips.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (trip is null)
        {
            return RecommendationErrors.TripNotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        var today = OutfitSources.Today(now);
        if (trip.EndDate < today)
        {
            return RecommendationErrors.TripOver.ToHttpResult();
        }

        var ask = TripAsk.From(TripStore.Request(trip), from: today);
        var plan = await TripPlanning.PlanAsync(db, weather, cache, userId, ask, now, cancellationToken);
        if (plan.IsFailure && plan.Error == ProfileErrors.PersonaNotFound)
        {
            plan = await TripPlanning.PlanAsync(db, weather, cache, userId, ask with { PersonaId = null }, now, cancellationToken);
        }

        if (plan.IsFailure)
        {
            return plan.Error.ToHttpResult();
        }

        TripStore.Apply(trip, plan.Value, now);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(await TripStore.RespondAsync(db, storage, trip, cancellationToken));
    }
}
