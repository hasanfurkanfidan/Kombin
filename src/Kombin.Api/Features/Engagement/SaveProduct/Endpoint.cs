using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Engagement;
using Npgsql;

namespace Kombin.Api.Features.Engagement.SaveProduct;

internal static class SaveProductEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPut("/{productId:guid}", Handle)
            .WithName("SaveProduct")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid productId, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        if (!await db.Products.AnyAsync(product => product.Id == productId, cancellationToken))
        {
            return CatalogErrors.ProductNotFound.ToHttpResult();
        }

        if (await db.SavedProducts.AnyAsync(saved => saved.UserId == userId && saved.ProductId == productId, cancellationToken))
        {
            return TypedResults.NoContent();
        }

        db.SavedProducts.Add(new SavedProduct { UserId = userId, ProductId = productId, CreatedAt = clock.UtcNow });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel request saved it first; the result is the same.
        }

        return TypedResults.NoContent();
    }
}
