using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification.MarkAllRead;

internal static class MarkAllReadEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/read-all", Handle)
            .WithName("MarkAllNotificationsRead")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        await db.Notifications
            .Where(notification => notification.UserId == userId && notification.ReadAt == null && notification.Status != NotificationStatus.Failed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAt, now), cancellationToken);

        return TypedResults.NoContent();
    }
}
