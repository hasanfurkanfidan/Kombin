using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Wardrobe.ListItems;

/// <summary>
/// <c>Category</c> is a category slug; a top-level one (<c>ust-giyim</c>) includes its children. <c>Color</c> matches the primary color.
/// <c>Archived=true</c> lists the removed items instead of the wardrobe.
/// </summary>
internal sealed record ListItemsQuery(string? Category, string? Color, bool? Archived, int Page = 1, int PageSize = PagedRequest.DefaultPageSize);

internal static class ListItemsEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/items", Handle)
            .WithName("ListWardrobeItems")
            .WithValidation<ListItemsQuery>()
            .Produces<PagedResponse<WardrobeItemSummary>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>Newest first. Processing and failed items are included (the grid shows them as such); unfinished uploads are not.</summary>
    private static async Task<IResult> Handle(
        [AsParameters] ListItemsQuery query, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var paging = new PagedRequest(query.Page, query.PageSize);
        var archived = query.Archived ?? false;
        var items = db.WardrobeItems.AsNoTracking().VisibleOf(currentUser.UserId).Where(item => item.IsArchived == archived);

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var slug = query.Category.Trim();
            var categoryIds = await db.Categories
                .Where(category => category.Slug == slug || category.Slug.StartsWith(slug + "/"))
                .Select(category => category.Id)
                .ToListAsync(cancellationToken);

            items = items.Where(item => item.CategoryId != null && categoryIds.Contains(item.CategoryId.Value));
        }

        if (EnumNames.TryParse<ProductColor>(query.Color, out var color))
        {
            items = items.Where(item => item.PrimaryColor == color);
        }

        var totalCount = await items.CountAsync(cancellationToken);
        var page = await items
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .ToListAsync(cancellationToken);

        var summaries = await WardrobeItemResponses.SummarizeAsync(db, storage, page, cancellationToken);

        return TypedResults.Ok(PagedResponse<WardrobeItemSummary>.Create(summaries, paging, totalCount));
    }
}
