using System.Text.Json;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification.ListNotifications;

/// <summary><c>Status</c> tells the app whether the push went out; the feed shows <c>Pending</c>, <c>Sent</c> and <c>Suppressed</c> alike.</summary>
internal sealed record NotificationResponse(
    long Id,
    NotificationKind Kind,
    string Title,
    string Body,
    JsonElement Data,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    NotificationStatus Status);

internal static class ListNotificationsEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListNotifications")
            .Produces<PagedResponse<NotificationResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle([AsParameters] PagedRequest paging, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var feed = db.Notifications.AsNoTracking().Where(notification => notification.UserId == userId && notification.Status != NotificationStatus.Failed);

        var totalCount = await feed.CountAsync(cancellationToken);
        var page = await feed
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .ToListAsync(cancellationToken);

        var items = page
            .Select(notification => new NotificationResponse(
                notification.Id,
                notification.Kind,
                notification.Title,
                notification.Body,
                JsonDocument.Parse(notification.Data).RootElement.Clone(),
                notification.CreatedAt,
                notification.ReadAt,
                notification.Status))
            .ToList();

        return Results.Ok(PagedResponse<NotificationResponse>.Create(items, paging, totalCount));
    }
}
