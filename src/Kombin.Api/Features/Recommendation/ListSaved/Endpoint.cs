using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.ListSaved;

internal static class ListSavedEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/saved", Handle)
            .WithName("ListSavedOutfits")
            .Produces<PagedResponse<OutfitResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>Outfits the user saved (worn ones included, dismissed ones not), most recently saved first.</summary>
    private static async Task<IResult> Handle(
        [AsParameters] PagedRequest paging, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var saved = db.Outfits.AsNoTracking()
            .Where(outfit => outfit.UserId == currentUser.UserId && outfit.SavedAt != null && outfit.Status != OutfitStatus.Dismissed);

        var totalCount = await saved.CountAsync(cancellationToken);
        var page = await saved
            .Include(outfit => outfit.Items)
            .OrderByDescending(outfit => outfit.SavedAt)
            .ThenByDescending(outfit => outfit.Id)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(PagedResponse<OutfitResponse>.Create(await OutfitResponses.BuildAsync(db, storage, page, cancellationToken), paging, totalCount));
    }
}
