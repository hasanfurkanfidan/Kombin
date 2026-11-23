using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.AskFriend;

namespace Kombin.Api.Features.AskFriend.ListAsks;

internal sealed record ListAsksQuery(int Page = 1, int PageSize = PagedRequest.DefaultPageSize);

/// <summary><c>GET /api/v1/me/asks</c>: "Sorduklarım", the caller's asks, newest first.</summary>
internal static class ListAsksEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListAsks")
            .Produces<PagedResponse<AskSummary>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(
        [AsParameters] ListAsksQuery query, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var paging = new PagedRequest(query.Page, query.PageSize);
        var userId = currentUser.UserId;
        var asks = db.Asks.AsNoTracking().Where(ask => ask.UserId == userId);

        var totalCount = await asks.CountAsync(cancellationToken);
        var page = await asks
            .OrderByDescending(ask => ask.CreatedAt)
            .ThenByDescending(ask => ask.Id)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .ToListAsync(cancellationToken);

        var ids = page.Select(ask => ask.Id).ToList();
        var counts = (await db.AskAnswers.AsNoTracking()
                .Where(answer => ids.Contains(answer.AskId) && answer.HiddenAt == null)
                .GroupBy(answer => new { answer.AskId, answer.Choice })
                .Select(group => new { group.Key.AskId, group.Key.Choice, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.AskId);

        var now = clock.UtcNow;
        var items = page.Select(ask =>
        {
            IReadOnlyDictionary<AskChoice, int> byChoice = counts[ask.Id].ToDictionary(row => row.Choice, row => row.Count);
            return new AskSummary(ask.Id, ask.Kind, AskState.Of(ask, now), ask.CreatedAt, ask.ExpiresAt, byChoice.Values.Sum(), AskResponses.Leading(byChoice));
        }).ToList();

        return TypedResults.Ok(PagedResponse<AskSummary>.Create(items, paging, totalCount));
    }
}
