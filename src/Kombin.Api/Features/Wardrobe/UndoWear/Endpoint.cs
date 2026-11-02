using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Features.Wardrobe.RecordWear;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Wardrobe.UndoWear;

internal static class UndoWearEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/items/{id:guid}/wear/{date}", Handle)
            .WithName("UndoWardrobeWear")
            .Produces<WearResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Removes the day (<c>yyyy-MM-dd</c>). Idempotent: a day that was not recorded leaves everything as it is.</summary>
    private static async Task<IResult> Handle(Guid id, DateOnly date, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.WardrobeItems.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == currentUser.UserId, cancellationToken);
        if (item is null)
        {
            return WardrobeErrors.ItemNotFound.ToHttpResult();
        }

        await db.WardrobeWears.Where(wear => wear.ItemId == item.Id && wear.WornOn == date).ExecuteDeleteAsync(cancellationToken);
        var summary = await WearSummaries.RefreshAsync(db, item, cancellationToken);

        return TypedResults.Ok(new WearResponse(item.Id, null, summary.WearCount, summary.LastWornAt));
    }
}
