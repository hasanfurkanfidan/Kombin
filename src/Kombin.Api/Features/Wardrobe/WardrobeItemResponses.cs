using System.Text.Json.Serialization;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe;

internal sealed record WardrobeCategoryRef(Guid Id, string Slug, string Name);

/// <summary>
/// One wardrobe item. <c>ThumbUrl</c>/<c>CutoutUrl</c> are public and permanent (a catalog item whose image could not be copied shows the
/// catalog's image); <c>OriginalUrl</c> is a 15-minute presigned link, given only when there is no cutout to show. <c>NeedsCategory</c>
/// asks the app to let the user pick one of <c>Analysis.CategoryCandidates</c>. <c>Analysis</c> is there once a photo was analysed.
/// </summary>
internal sealed record WardrobeItemResponse(
    Guid Id,
    WardrobeItemStatus Status,
    string? Name,
    WardrobeCategoryRef? Category,
    bool NeedsCategory,
    ProductColor PrimaryColor,
    ProductColor? SecondaryColor,
    ProductPattern Pattern,
    ProductFit Fit,
    int Formality,
    IReadOnlyList<string> Seasons,
    BrandRef? Brand,
    Guid? ProductId,
    bool FromCatalog,
    string? Size,
    decimal? PurchasePrice,
    DateOnly? PurchasedAt,
    string? ThumbUrl,
    string? CutoutUrl,
    string? OriginalUrl,
    IReadOnlyList<string> Warnings,
    string? FailureReason,
    StoredAnalysis? Analysis,
    int WearCount,
    DateOnly? LastWornAt,
    bool IsArchived,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// An item in lists (the wardrobe grid, the stats' most and least worn). <c>CompanionCount</c>, how many pieces of the wardrobe it pairs
/// with, is present only on <c>GET /items</c>; an item that cannot be worn (yet) has 0.
/// </summary>
internal sealed record WardrobeItemSummary(
    Guid Id,
    WardrobeItemStatus Status,
    string? Name,
    string? CategorySlug,
    bool NeedsCategory,
    ProductColor PrimaryColor,
    string? ThumbUrl,
    int WearCount,
    DateOnly? LastWornAt,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CompanionCount = null);

internal static class WardrobeItemResponses
{
    /// <summary>The items of a user, without the uploads that were started but never processed (the app never finished them).</summary>
    public static IQueryable<WardrobeItem> VisibleOf(this IQueryable<WardrobeItem> items, Guid userId) =>
        items.Where(item => item.UserId == userId && !(item.Status == WardrobeItemStatus.Processing && item.ProcessRequestedAt == null));

    public static async Task<WardrobeItemResponse> BuildAsync(KombinDbContext db, IObjectStorage storage, WardrobeItem item, CancellationToken cancellationToken)
    {
        var category = item.CategoryId is { } categoryId
            ? await db.Categories.AsNoTracking().Where(candidate => candidate.Id == categoryId).Select(candidate => new WardrobeCategoryRef(candidate.Id, candidate.Slug, candidate.Name)).SingleOrDefaultAsync(cancellationToken)
            : null;

        var brand = item.BrandId is { } brandId
            ? await db.Brands.AsNoTracking().Where(candidate => candidate.Id == brandId).Select(candidate => new BrandRef(candidate.Id, candidate.Name, candidate.Slug)).SingleOrDefaultAsync(cancellationToken)
            : null;

        var fallbackImage = await CatalogFallbackImagesAsync(db, [item], cancellationToken);
        var (thumbUrl, cutoutUrl) = ImageUrls(storage, item, fallbackImage);

        // The original is private; it is only worth a presigned link when there is no cutout to show (a failed analysis).
        var originalUrl = cutoutUrl is null && item.ImageOriginalKey is { } originalKey && item.Status != WardrobeItemStatus.Processing
            ? storage.GetPresignedDownloadUrl(originalKey, WardrobeLimits.OriginalUrlLifetime)
            : null;

        return new WardrobeItemResponse(
            item.Id,
            item.Status,
            item.Name,
            category,
            NeedsCategory(item),
            item.PrimaryColor,
            item.SecondaryColor,
            item.Pattern,
            item.Fit,
            item.Formality,
            EnumNames.SeasonNames(item.Seasons),
            brand,
            item.ProductId,
            item.FromCatalog,
            item.Size,
            item.PurchasePrice,
            item.PurchasedAt,
            thumbUrl,
            cutoutUrl,
            originalUrl,
            item.Warnings,
            item.Status == WardrobeItemStatus.Failed ? item.FailureReason : null,
            item.Status == WardrobeItemStatus.Processing ? null : WardrobeAnalysis.Read(item.AnalysisJson),
            item.WearCount,
            item.LastWornAt,
            item.IsArchived,
            item.ConfirmedAt,
            item.CreatedAt,
            item.UpdatedAt);
    }

    public static async Task<List<WardrobeItemSummary>> SummarizeAsync(
        KombinDbContext db, IObjectStorage storage, IReadOnlyList<WardrobeItem> items, CancellationToken cancellationToken)
    {
        var categoryIds = items.Select(item => item.CategoryId).OfType<Guid>().Distinct().ToList();
        var slugs = await db.Categories.AsNoTracking()
            .Where(category => categoryIds.Contains(category.Id))
            .ToDictionaryAsync(category => category.Id, category => category.Slug, cancellationToken);

        var fallbackImages = await CatalogFallbackImagesAsync(db, items, cancellationToken);

        return
        [
            .. items.Select(item => new WardrobeItemSummary(
                item.Id,
                item.Status,
                item.Name,
                item.CategoryId is { } categoryId ? slugs.GetValueOrDefault(categoryId) : null,
                NeedsCategory(item),
                item.PrimaryColor,
                ImageUrls(storage, item, fallbackImages).ThumbUrl,
                item.WearCount,
                item.LastWornAt,
                item.IsArchived,
                item.CreatedAt)),
        ];
    }

    /// <summary>A ready (or failed, then hand-filled) item without a category: the app asks the user to pick one.</summary>
    public static bool NeedsCategory(WardrobeItem item) => item.Status != WardrobeItemStatus.Processing && item.CategoryId is null;

    private static (string? ThumbUrl, string? CutoutUrl) ImageUrls(IObjectStorage storage, WardrobeItem item, Dictionary<Guid, string> fallbackImages)
    {
        var fallback = item.ProductId is { } productId ? fallbackImages.GetValueOrDefault(productId) : null;

        return (
            item.ImageThumbKey is { } thumb ? storage.GetPublicUrl(thumb) : fallback,
            item.ImageCutoutKey is { } cutout ? storage.GetPublicUrl(cutout) : fallback);
    }

    /// <summary>The catalog image of catalog items whose own copy is missing (the copy failed), by product ID.</summary>
    private static async Task<Dictionary<Guid, string>> CatalogFallbackImagesAsync(KombinDbContext db, IEnumerable<WardrobeItem> items, CancellationToken cancellationToken)
    {
        var productIds = items
            .Where(item => item.FromCatalog && item.ImageThumbKey is null)
            .Select(item => item.ProductId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        if (productIds.Count == 0)
        {
            return [];
        }

        return await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id) && product.PrimaryImageUrl != null)
            .ToDictionaryAsync(product => product.Id, product => product.PrimaryImageUrl!, cancellationToken);
    }
}
