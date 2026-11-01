using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Engagement.ListSaved;

/// <summary>
/// The catalog's <see cref="ProductListItem"/> fields, flattened, plus <c>isActive</c> (saved products stay in the list after they
/// are delisted) and <c>savedAt</c>. A product nobody sells has <c>offerCount</c> 0 and <c>lowestPrice</c> 0.
/// </summary>
internal sealed record SavedProductItem(
    Guid Id,
    string Name,
    string Slug,
    BrandRef Brand,
    CategoryRef Category,
    string? PrimaryImageUrl,
    ProductColor PrimaryColor,
    decimal LowestPrice,
    decimal? LowestOriginalPrice,
    int OfferCount,
    int DiscountPercent,
    bool IsActive,
    DateTimeOffset SavedAt)
{
    public static SavedProductItem From(ProductCard card, DateTimeOffset savedAt)
    {
        var item = card.ToListItem();

        return new SavedProductItem(
            item.Id,
            item.Name,
            item.Slug,
            item.Brand,
            item.Category,
            item.PrimaryImageUrl,
            item.PrimaryColor,
            item.LowestPrice,
            item.LowestOriginalPrice,
            item.OfferCount,
            item.DiscountPercent,
            card.IsActive,
            savedAt);
    }
}

internal static class ListSavedEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListSaved")
            .Produces<PagedResponse<SavedProductItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle([AsParameters] PagedRequest paging, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var saved = db.SavedProducts.AsNoTracking().Where(candidate => candidate.UserId == userId);

        var totalCount = await saved.CountAsync(cancellationToken);
        var page = await saved
            .OrderByDescending(candidate => candidate.CreatedAt)
            .ThenByDescending(candidate => candidate.ProductId)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .Select(candidate => new { candidate.ProductId, candidate.CreatedAt })
            .ToListAsync(cancellationToken);

        var cards = await ProductSummaries.LoadAsync(db, [.. page.Select(row => row.ProductId)], cancellationToken);

        var items = page
            .Select(row => SavedProductItem.From(cards[row.ProductId], row.CreatedAt))
            .ToList();

        return Results.Ok(PagedResponse<SavedProductItem>.Create(items, paging, totalCount));
    }
}
