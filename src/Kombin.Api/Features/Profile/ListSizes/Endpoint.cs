using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Profile.ListSizes;

internal sealed record SizeListResponse(IReadOnlyList<SizeEntryResponse> Items);

internal static class ListSizesEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListSizes")
            .Produces<SizeListResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var rows = await db.SizeEntries
            .AsNoTracking()
            .Where(entry => entry.UserId == userId)
            .Join(db.Brands, entry => entry.BrandId, brand => brand.Id, (entry, brand) => new { Entry = entry, Brand = brand })
            .OrderBy(row => row.Brand.Name)
            .ThenBy(row => row.Entry.Group)
            .ToListAsync(cancellationToken);

        return Results.Ok(new SizeListResponse([.. rows.Select(row => SizeEntryResponse.From(row.Entry, row.Brand))]));
    }
}
