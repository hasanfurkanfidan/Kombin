using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Recommendation.WearOutfit;

internal static class WearOutfitEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/worn", Handle)
            .WithName("WearOutfit")
            .Produces<OutfitResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>Marks the outfit's wardrobe pieces worn today, exactly as <c>POST /me/wardrobe/items/{id}/wear</c> would.</summary>
    private static async Task<IResult> Handle(
        Guid id, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IClock clock, CancellationToken cancellationToken)
    {
        var outfit = await OutfitActions.FindAsync(db, currentUser.UserId, id, cancellationToken);
        if (outfit is null)
        {
            return RecommendationErrors.OutfitNotFound.ToHttpResult();
        }

        var result = await OutfitActions.WearAsync(db, outfit, clock.UtcNow, cancellationToken);
        if (result.IsFailure)
        {
            return result.ToHttpResult();
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(await OutfitActions.RespondAsync(db, storage, outfit, cancellationToken));
    }
}
