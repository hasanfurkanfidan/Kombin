using Kombin.Api.Common.Results;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Catalog.SweepOffers;

/// <summary>The body is optional; without a seller the whole catalog is swept.</summary>
internal sealed record SweepOffersRequest(string? SellerSlug);

internal sealed record SweepOffersResponse(int OffersDeactivated, int ProductsDeactivated, int ProductsReactivated);

internal static class SweepOffersEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/offers/sweep", Handle)
            .WithName("SweepOffers")
            .Produces<SweepOffersResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Runs the nightly <see cref="OfferSweepJob"/> on demand, in the request.</summary>
    private static async Task<IResult> Handle(SweepOffersRequest? request, KombinDbContext db, OfferSweepJob job, CancellationToken cancellationToken)
    {
        Guid? sellerId = null;

        if (!string.IsNullOrWhiteSpace(request?.SellerSlug))
        {
            var slug = request.SellerSlug.Trim();
            sellerId = await db.Sellers.Where(seller => seller.Slug == slug).Select(seller => (Guid?)seller.Id).SingleOrDefaultAsync(cancellationToken);

            if (sellerId is null)
            {
                return CatalogErrors.SellerNotFound.ToHttpResult();
            }
        }

        var summary = await job.SweepAsync(sellerId, cancellationToken);

        return Results.Ok(new SweepOffersResponse(summary.OffersDeactivated, summary.ProductsDeactivated, summary.ProductsReactivated));
    }
}
