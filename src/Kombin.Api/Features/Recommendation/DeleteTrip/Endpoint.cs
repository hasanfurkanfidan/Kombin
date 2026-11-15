using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Recommendation.DeleteTrip;

internal static class DeleteTripEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}", Handle)
            .WithName("DeleteTrip")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Deletes a saved trip for good (the row holds a destination and dates). Someone else's trip is 404.</summary>
    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var deleted = await db.Trips.Where(trip => trip.Id == id && trip.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        return deleted == 0 ? RecommendationErrors.TripNotFound.ToHttpResult() : TypedResults.NoContent();
    }
}
