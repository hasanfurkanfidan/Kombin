using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Catalog;

internal static class CatalogErrors
{
    public static readonly Error ProductNotFound =
        Error.NotFound("Catalog.ProductNotFound", "The product does not exist.");

    public static readonly Error CategoryNotFound =
        Error.NotFound("Catalog.CategoryNotFound", "The category does not exist.");

    public static readonly Error SellerNotFound =
        Error.NotFound("Catalog.SellerNotFound", "The seller does not exist.");

    public static readonly Error BrandNotFound =
        Error.NotFound("Catalog.BrandNotFound", "The brand does not exist.");

    // Raised by InternalKeyFilter in Common, which cannot depend on a feature; the code is part of the Catalog API contract.
    public static readonly Error InvalidInternalKey = InternalKeyFilter.InvalidKey;

    public static readonly Error BatchTooLarge =
        Error.Validation("Catalog.BatchTooLarge", $"A batch may contain at most {UpsertLimits.MaxItems} items.");

    // Reported per item in an upsert response when an item could not be stored even on its own.
    public static readonly Error ItemFailed =
        Error.Failure("Catalog.ItemFailed", "The item could not be stored.");
}

internal static class UpsertLimits
{
    public const int MaxItems = 500;

    public const int GroupSize = 50;
}
