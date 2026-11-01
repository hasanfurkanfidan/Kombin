using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Engagement.UnsaveProduct;

internal static class UnsaveProductEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{productId:guid}", Handle)
            .WithName("UnsaveProduct")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(Guid productId, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        // Removing something that is not saved is fine: the end state is what the caller asked for.
        await db.SavedProducts
            .Where(saved => saved.UserId == userId && saved.ProductId == productId)
            .ExecuteDeleteAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
