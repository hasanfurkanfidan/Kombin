using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Engagement.DeleteAlert;

internal static class DeleteAlertEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}", Handle)
            .WithName("DeleteAlert")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        // The foreign key on alert_triggers is ON DELETE SET NULL, so the triggers stay in the user's feed.
        var deleted = await db.PriceAlerts
            .Where(alert => alert.Id == id && alert.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 0 ? EngagementErrors.AlertNotFound.ToHttpResult() : TypedResults.NoContent();
    }
}
