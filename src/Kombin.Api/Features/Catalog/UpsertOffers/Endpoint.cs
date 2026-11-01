using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;

namespace Kombin.Api.Features.Catalog.UpsertOffers;

internal sealed record UpsertOffersRequest(string SellerSlug, IReadOnlyList<UpsertOfferItem> Items);

internal sealed record UpsertOfferItem(
    string ExternalId,
    string Url,
    string Title,
    decimal Price,
    decimal? OriginalPrice,
    string? Currency,
    decimal? ShippingCost,
    decimal? FreeShippingThreshold,
    string StockStatus,
    IReadOnlyList<string>? AvailableSizes,
    IReadOnlyList<string>? ImageUrls,
    UpsertProductInfo Product);

internal sealed record UpsertProductInfo(
    string BrandName,
    string Name,
    string? ModelCode,
    string CategorySlug,
    string Audience,
    string PrimaryColor,
    string? Pattern,
    string? Fit,
    int? Formality,
    IReadOnlyList<string>? Seasons,
    string? Material,
    string? Description,
    Guid? CanonicalProductId);

internal sealed record UpsertItemError(int Index, string ExternalId, string ErrorCode, string Message);

internal sealed record UpsertOffersResponse(int Created, int Updated, int SnapshotsWritten, IReadOnlyList<UpsertItemError> Errors);

internal static class UpsertOffersEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/offers/upsert", Handle)
            .WithName("UpsertOffers")
            .WithValidation<UpsertOffersRequest>()
            .Produces<UpsertOffersResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(UpsertOffersRequest request, OfferUpserter upserter, CancellationToken cancellationToken)
    {
        var result = await upserter.UpsertAsync(request.SellerSlug, request.Items, cancellationToken);

        return result.ToHttpResult(outcome => Results.Ok(new UpsertOffersResponse(
            outcome.Created,
            outcome.Updated,
            outcome.SnapshotsWritten,
            [.. outcome.Failures.Select(failure => new UpsertItemError(failure.Index, failure.ExternalId, failure.Error.Code, failure.Error.Description))])));
    }
}
