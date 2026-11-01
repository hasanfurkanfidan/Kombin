using Kombin.Api.Common.Pagination;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Catalog.ListBrands;

internal sealed record ListBrandsQuery(string? Query, int Page = 1, int PageSize = PagedRequest.DefaultPageSize);

internal sealed record BrandItem(Guid Id, string Name, string Slug, string? LogoUrl, string? WebsiteUrl);

internal static class ListBrandsEndpoint
{
    private const string LikeEscape = "\\";

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/brands", Handle)
            .WithName("ListBrands")
            .Produces<PagedResponse<BrandItem>>();

    private static async Task<IResult> Handle([AsParameters] ListBrandsQuery query, KombinDbContext db, CancellationToken cancellationToken)
    {
        var paging = new PagedRequest(query.Page, query.PageSize);
        var brands = db.Brands.AsNoTracking();
        var term = query.Query?.Trim();

        if (string.IsNullOrEmpty(term))
        {
            brands = brands.OrderBy(brand => brand.Name);
        }
        else
        {
            var escaped = term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            var contains = $"%{escaped}%";
            var prefix = $"{escaped}%";

            // Contains match, with names that start with the term first.
            brands = brands
                .Where(brand => EF.Functions.ILike(brand.Name, contains, LikeEscape))
                .OrderByDescending(brand => EF.Functions.ILike(brand.Name, prefix, LikeEscape))
                .ThenBy(brand => brand.Name);
        }

        var totalCount = await brands.CountAsync(cancellationToken);
        var items = await brands
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .Select(brand => new BrandItem(brand.Id, brand.Name, brand.Slug, brand.LogoUrl, brand.WebsiteUrl))
            .ToListAsync(cancellationToken);

        return Results.Ok(PagedResponse<BrandItem>.Create(items, paging, totalCount));
    }
}
