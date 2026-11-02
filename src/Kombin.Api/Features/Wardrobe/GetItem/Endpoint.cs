using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Wardrobe.GetItem;

internal static class GetItemEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/items/{id:guid}", Handle)
            .WithName("GetWardrobeItem")
            .Produces<WardrobeItemResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Archived items are still returned (with <c>isArchived</c>); the app polls this until <c>status</c> leaves <c>Processing</c>.</summary>
    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var item = await db.WardrobeItems.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == currentUser.UserId, cancellationToken);

        return item is null
            ? WardrobeErrors.ItemNotFound.ToHttpResult()
            : TypedResults.Ok(await WardrobeItemResponses.BuildAsync(db, storage, item, cancellationToken));
    }
}
