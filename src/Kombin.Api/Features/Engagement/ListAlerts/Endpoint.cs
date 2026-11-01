using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Engagement.ListAlerts;

internal sealed record ListAlertsResponse(IReadOnlyList<AlertResponse> Items);

internal static class ListAlertsEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListAlerts")
            .Produces<ListAlertsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary><paramref name="active"/> filters on the alert's switch and defaults to the live alerts; a user's alerts are few, so there is no paging.</summary>
    private static async Task<IResult> Handle(bool? active, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var wantActive = active ?? true;

        var alerts = await db.PriceAlerts
            .AsNoTracking()
            .Where(alert => alert.UserId == userId && alert.IsActive == wantActive)
            .OrderByDescending(alert => alert.CreatedAt)
            .ThenByDescending(alert => alert.Id)
            .ToListAsync(cancellationToken);

        return Results.Ok(new ListAlertsResponse(await AlertResponses.BuildAsync(db, alerts, cancellationToken)));
    }
}
