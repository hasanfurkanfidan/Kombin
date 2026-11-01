using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Notification.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.Features.Notification.SendTest;

internal sealed record SendTestRequest(Guid UserId, string? Title, string? Body);

/// <summary>The notification as the push sender left it, and what Expo said about each device it was sent to.</summary>
internal sealed record SendTestResponse(
    long NotificationId,
    NotificationStatus Status,
    string? FailureReason,
    string? TicketId,
    IReadOnlyList<PushDelivery> Deliveries);

internal static class SendTestEndpoint
{
    public const string DefaultTitle = "Kombin test bildirimi";

    public const string DefaultBody = "Bu bir test bildirimidir.";

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/test", Handle)
            .WithName("SendTestNotification")
            .WithValidation<SendTestRequest>()
            .Produces<SendTestResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// For QA on a real phone: creates a <c>System</c> notification (preferences and quiet hours do not apply) and pushes it right away,
    /// in the request, instead of waiting for the push sender.
    /// </summary>
    private static async Task<IResult> Handle(SendTestRequest request, KombinDbContext db, IClock clock, PushSenderJob sender, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(user => user.Id == request.UserId, cancellationToken))
        {
            return NotificationErrors.UserNotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        var notification = new NotificationEntity
        {
            UserId = request.UserId,
            Kind = NotificationKind.System,
            Title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle : request.Title.Trim(),
            Body = string.IsNullOrWhiteSpace(request.Body) ? DefaultBody : request.Body.Trim(),
            Data = NotificationData.ForSystem(),
            DedupeKey = $"system:{Guid.NewGuid():N}",
            CreatedAt = now,
            ScheduledFor = now,
            Status = NotificationStatus.Pending,
        };

        db.Notifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);

        var run = await sender.SendDueAsync(request.UserId, notification.Id, cancellationToken);

        return TypedResults.Ok(new SendTestResponse(notification.Id, notification.Status, notification.FailureReason, notification.ExpoTicketId, run.Deliveries));
    }
}
