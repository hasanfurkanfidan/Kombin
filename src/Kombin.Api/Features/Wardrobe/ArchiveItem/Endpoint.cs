using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Wardrobe.ArchiveItem;

internal static class ArchiveItemEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/items/{id:guid}", Handle)
            .WithName("ArchiveWardrobeItem")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>A soft delete: the item leaves the wardrobe, its wear history stays, and <c>wardrobe-cleanup</c> deletes its images 30 days later.</summary>
    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var item = await db.WardrobeItems.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == currentUser.UserId, cancellationToken);
        if (item is null)
        {
            return WardrobeErrors.ItemNotFound.ToHttpResult();
        }

        if (!item.IsArchived)
        {
            item.IsArchived = true;
            item.ArchivedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }
}
