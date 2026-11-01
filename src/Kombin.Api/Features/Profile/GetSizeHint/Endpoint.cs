using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Profile.GetSizeHint;

internal static class GetSizeHintEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/hint", Handle)
            .WithName("GetSizeHint")
            .Produces<SizeHintResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid productId, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .Where(candidate => candidate.Id == productId)
            .Select(candidate => new { candidate.BrandId, CategorySlug = candidate.Category.Slug })
            .SingleOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            return CatalogErrors.ProductNotFound.ToHttpResult();
        }

        return Results.Ok(await SizeHints.BuildAsync(db, currentUser.UserId, product.BrandId, product.CategorySlug, cancellationToken));
    }
}
