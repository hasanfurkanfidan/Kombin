using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Recommendation.SaveTrip;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Recommendation.GetTrip;

internal static class GetTripEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}", Handle)
            .WithName("GetTrip")
            .Produces<TripResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>A saved trip with its plan as it was last planned; wardrobe names and images are today's. Someone else's trip is 404.</summary>
    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var trip = await db.Trips.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);

        return trip is null
            ? RecommendationErrors.TripNotFound.ToHttpResult()
            : TypedResults.Ok(await TripStore.RespondAsync(db, storage, trip, cancellationToken));
    }
}
