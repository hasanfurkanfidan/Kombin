using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.UpdateItem;

/// <summary>
/// A partial update: a null field is left as it is, and <c>Clear</c> names the optional fields to empty (<see cref="UpdateItemRequest.ClearableFields"/>).
/// An empty body is valid: it is the one-tap "looks right" confirmation. Enum values are names, matched case-insensitively.
/// </summary>
internal sealed record UpdateItemRequest(
    string? Name,
    string? CategorySlug,
    string? PrimaryColor,
    string? SecondaryColor,
    string? Pattern,
    string? Fit,
    short? Formality,
    IReadOnlyList<string>? Seasons,
    Guid? BrandId,
    string? Size,
    decimal? PurchasePrice,
    DateOnly? PurchasedAt,
    IReadOnlyList<string>? Clear)
{
    public static readonly IReadOnlyList<string> ClearableFields = ["name", "secondaryColor", "brandId", "size", "purchasePrice", "purchasedAt"];

    public bool Clears(string field) => Clear?.Contains(field, StringComparer.OrdinalIgnoreCase) == true;
}

internal static class UpdateItemEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPatch("/items/{id:guid}", Handle)
            .WithName("UpdateWardrobeItem")
            .WithValidation<UpdateItemRequest>()
            .Produces<WardrobeItemResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// The first PATCH of a ready item records <c>ConfirmedAt</c>. A failed item can be filled in by hand: any PATCH makes it ready.
    /// A processing item cannot be edited (the analysis would overwrite the edit), nor can an archived one.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id,
        UpdateItemRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var item = await db.WardrobeItems.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == currentUser.UserId, cancellationToken);
        if (item is null)
        {
            return WardrobeErrors.ItemNotFound.ToHttpResult();
        }

        if (item.IsArchived)
        {
            return WardrobeErrors.ItemArchived.ToHttpResult();
        }

        if (item.Status == WardrobeItemStatus.Processing)
        {
            return WardrobeErrors.ItemProcessing.ToHttpResult();
        }

        if (request.CategorySlug is { } slug)
        {
            var categoryId = await db.Categories.Where(category => category.Slug == slug.Trim()).Select(category => (Guid?)category.Id).SingleOrDefaultAsync(cancellationToken);
            if (categoryId is null)
            {
                return WardrobeErrors.UnknownCategory.ToHttpResult();
            }

            item.CategoryId = categoryId;
        }

        if (request.BrandId is { } brandId)
        {
            if (!await db.Brands.AnyAsync(brand => brand.Id == brandId, cancellationToken))
            {
                return WardrobeErrors.UnknownBrand.ToHttpResult();
            }

            item.BrandId = brandId;
        }

        Apply(item, request);

        if (item.Status == WardrobeItemStatus.Failed)
        {
            item.Status = WardrobeItemStatus.Ready;
            item.FailureReason = null;
        }

        item.ConfirmedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(await WardrobeItemResponses.BuildAsync(db, storage, item, cancellationToken));
    }

    private static void Apply(WardrobeItem item, UpdateItemRequest request)
    {
        if (request.Name is { } name)
        {
            item.Name = name.Trim();
        }

        if (EnumNames.TryParse<ProductColor>(request.PrimaryColor, out var primary))
        {
            item.PrimaryColor = primary;
        }

        if (EnumNames.TryParse<ProductColor>(request.SecondaryColor, out var secondary))
        {
            item.SecondaryColor = secondary;
        }

        if (EnumNames.TryParse<ProductPattern>(request.Pattern, out var pattern))
        {
            item.Pattern = pattern;
        }

        if (EnumNames.TryParse<ProductFit>(request.Fit, out var fit))
        {
            item.Fit = fit;
        }

        if (request.Formality is { } formality)
        {
            item.Formality = formality;
        }

        if (request.Seasons is { } seasons)
        {
            item.Seasons = EnumNames.ParseSeasons(seasons);
        }

        if (request.Size is { } size)
        {
            item.Size = size.Trim();
        }

        if (request.PurchasePrice is { } price)
        {
            item.PurchasePrice = price;
        }

        if (request.PurchasedAt is { } purchasedAt)
        {
            item.PurchasedAt = purchasedAt;
        }

        if (request.Clears("name"))
        {
            item.Name = null;
        }

        if (request.Clears("secondaryColor"))
        {
            item.SecondaryColor = null;
        }

        if (request.Clears("brandId"))
        {
            item.BrandId = null;
        }

        if (request.Clears("size"))
        {
            item.Size = null;
        }

        if (request.Clears("purchasePrice"))
        {
            item.PurchasePrice = null;
        }

        if (request.Clears("purchasedAt"))
        {
            item.PurchasedAt = null;
        }
    }
}
