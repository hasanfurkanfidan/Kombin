using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Catalog.ListCategories;

internal sealed record CategoryNode(Guid Id, string Slug, string Name, IReadOnlyList<CategoryNode> Children);

internal static class ListCategoriesEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/categories", Handle)
            .WithName("ListCategories")
            .Produces<IReadOnlyList<CategoryNode>>();

    private static async Task<IResult> Handle(KombinDbContext db, CancellationToken cancellationToken)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Name)
            .ToListAsync(cancellationToken);

        var byParent = categories.ToLookup(category => category.ParentId);

        IReadOnlyList<CategoryNode> Build(Guid? parentId) =>
            [.. byParent[parentId].Select(category => new CategoryNode(category.Id, category.Slug, category.Name, Build(category.Id)))];

        return Results.Ok(Build(null));
    }
}
