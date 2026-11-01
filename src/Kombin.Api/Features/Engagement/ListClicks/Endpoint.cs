using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Engagement.ListClicks;

internal sealed record ClickSellerRef(Guid Id, string Name, string Slug);

internal sealed record ClickResponse(long Id, Guid OfferId, ProductSummary Product, ClickSellerRef Seller, string Source, DateTimeOffset ClickedAt);

internal static class ListClicksEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListClicks")
            .Produces<PagedResponse<ClickResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle([AsParameters] PagedRequest paging, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var clicks = db.Clicks.AsNoTracking().Where(click => click.UserId == userId);

        var totalCount = await clicks.CountAsync(cancellationToken);
        var page = await clicks
            .OrderByDescending(click => click.ClickedAt)
            .ThenByDescending(click => click.Id)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .ToListAsync(cancellationToken);

        var sellerIds = page.Select(click => click.SellerId).Distinct().ToList();
        var sellers = await db.Sellers
            .AsNoTracking()
            .Where(seller => sellerIds.Contains(seller.Id))
            .ToDictionaryAsync(seller => seller.Id, seller => new ClickSellerRef(seller.Id, seller.Name, seller.Slug), cancellationToken);

        var cards = await ProductSummaries.LoadAsync(db, [.. page.Select(click => click.ProductId).Distinct()], cancellationToken);

        var items = page
            .Select(click => new ClickResponse(
                click.Id,
                click.OfferId,
                cards[click.ProductId].ToSummary(),
                sellers[click.SellerId],
                click.Source,
                click.ClickedAt))
            .ToList();

        return Results.Ok(PagedResponse<ClickResponse>.Create(items, paging, totalCount));
    }
}
