using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification.MarkRead;

internal static class MarkReadEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:long}/read", Handle)
            .WithName("MarkNotificationRead")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(long id, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var own = db.Notifications.Where(notification => notification.Id == id && notification.UserId == userId && notification.Status != NotificationStatus.Failed);

        if (!await own.AnyAsync(cancellationToken))
        {
            return NotificationErrors.NotFound.ToHttpResult();
        }

        // Idempotent: a notification that was read before keeps its first read time.
        var now = clock.UtcNow;
        await own.Where(notification => notification.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAt, now), cancellationToken);

        return TypedResults.NoContent();
    }
}
