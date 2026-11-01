using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Profile.DeleteSize;

internal static class DeleteSizeEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}", Handle)
            .WithName("DeleteSize")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var entry = await db.SizeEntries.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (entry is null)
        {
            return ProfileErrors.SizeEntryNotFound.ToHttpResult();
        }

        db.SizeEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
