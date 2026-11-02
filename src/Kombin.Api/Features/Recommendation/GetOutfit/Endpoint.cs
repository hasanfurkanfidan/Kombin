using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Recommendation.GetOutfit;

internal static class GetOutfitEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}", Handle)
            .WithName("GetOutfit")
            .Produces<OutfitResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>One outfit as stored; also how the app picks up a narration upgraded in the background.</summary>
    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var outfit = await db.Outfits.AsNoTracking()
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == currentUser.UserId, cancellationToken);

        return outfit is null
            ? RecommendationErrors.OutfitNotFound.ToHttpResult()
            : TypedResults.Ok(await OutfitActions.RespondAsync(db, storage, outfit, cancellationToken));
    }
}
