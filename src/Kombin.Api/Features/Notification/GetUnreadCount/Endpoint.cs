using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification.GetUnreadCount;

internal sealed record UnreadCountResponse(int Count);

internal static class GetUnreadCountEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/unread-count", Handle)
            .WithName("GetUnreadNotificationCount")
            .Produces<UnreadCountResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>Counts what the feed shows (everything but <c>Failed</c>) that has not been read.</summary>
    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var count = await db.Notifications.CountAsync(
            notification => notification.UserId == userId && notification.ReadAt == null && notification.Status != NotificationStatus.Failed,
            cancellationToken);

        return TypedResults.Ok(new UnreadCountResponse(count));
    }
}
