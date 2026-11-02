using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.Features.Notification.Jobs;

/// <summary>What Expo said about one message (one notification to one device). <c>Status</c> is <c>ok</c>, <c>error</c> or <c>unreachable</c>.</summary>
internal sealed record PushDelivery(long NotificationId, Guid DeviceId, string ExpoPushToken, string Status, string? TicketId, string? Error);

internal sealed record PushRunResult(int Notifications, IReadOnlyList<PushDelivery> Deliveries)
{
    public static readonly PushRunResult Empty = new(0, []);
}

/// <summary>
/// Sends due <see cref="NotificationStatus.Pending"/> notifications through Expo, one message per active device of the user.
/// Notifications are sent in batches of <see cref="BatchSize"/> messages and a notification never spans two batches, so every
/// batch is saved on its own. A failed call leaves the row <c>Pending</c> for the next run; the <see cref="NotificationEntity.MaxAttempts"/>th
/// failure makes it <c>Failed</c>.
/// </summary>
internal sealed partial class PushSenderJob(KombinDbContext db, IExpoPushClient expo, IClock clock, ILogger<PushSenderJob> logger)
{
    public const string JobId = "push-sender";

    public const int BatchSize = 100;

    public const int MaxNotificationsPerRun = 500;

    private sealed record Outgoing(Device Device, ExpoPushMessage Message);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await SendDueAsync(null, null, cancellationToken);

        if (result.Notifications > 0)
        {
            LogRun(logger, result.Notifications, result.Deliveries.Count);
        }
    }

    /// <summary>
    /// Sends what is due now. <paramref name="userId"/> and <paramref name="notificationId"/> narrow the run to one user or one
    /// notification (the internal test endpoint, tests); the scheduled job passes neither.
    /// </summary>
    public async Task<PushRunResult> SendDueAsync(Guid? userId, long? notificationId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var due = db.Notifications.Where(notification => notification.Status == NotificationStatus.Pending && notification.ScheduledFor <= now);
        if (userId is { } onlyUser)
        {
            due = due.Where(notification => notification.UserId == onlyUser);
        }

        if (notificationId is { } onlyNotification)
        {
            due = due.Where(notification => notification.Id == onlyNotification);
        }

        var notifications = await due
            .OrderBy(notification => notification.ScheduledFor)
            .ThenBy(notification => notification.Id)
            .Take(MaxNotificationsPerRun)
            .ToListAsync(cancellationToken);

        if (notifications.Count == 0)
        {
            return PushRunResult.Empty;
        }

        var userIds = notifications.Select(notification => notification.UserId).Distinct().ToList();
        var devices = (await db.Devices
            .Where(device => device.IsActive && userIds.Contains(device.UserId))
            .OrderByDescending(device => device.LastSeenAt)
            .ToListAsync(cancellationToken)).ToLookup(device => device.UserId);

        var deliveries = new List<PushDelivery>();
        var batch = new List<(NotificationEntity Notification, List<Outgoing> Messages)>();
        var batchSize = 0;

        foreach (var notification in notifications)
        {
            var targets = devices[notification.UserId].Take(BatchSize).ToList();

            if (targets.Count == 0)
            {
                // Every device went away between the dispatcher and now; the notification stays visible in the feed.
                notification.Status = NotificationStatus.Suppressed;
                notification.FailureReason = NotificationEntity.NoDevice;
                continue;
            }

            if (batchSize + targets.Count > BatchSize)
            {
                await FlushAsync(batch, now, deliveries, cancellationToken);
                batchSize = 0;
            }

            var data = NotificationData.ForPush(notification.Data, notification.Id, notification.Kind);
            batch.Add((notification, [.. targets.Select(device => new Outgoing(device, new ExpoPushMessage(device.ExpoPushToken, notification.Title, notification.Body, data)))]));
            batchSize += targets.Count;
        }

        await FlushAsync(batch, now, deliveries, cancellationToken);

        return new PushRunResult(notifications.Count, deliveries);
    }

    /// <summary>Sends one batch, applies the outcome to its notifications and devices and saves (together with anything staged before).</summary>
    private async Task FlushAsync(
        List<(NotificationEntity Notification, List<Outgoing> Messages)> batch,
        DateTimeOffset now,
        List<PushDelivery> deliveries,
        CancellationToken cancellationToken)
    {
        if (batch.Count > 0)
        {
            var messages = batch.SelectMany(entry => entry.Messages).ToList();
            var result = await expo.SendAsync([.. messages.Select(message => message.Message)], cancellationToken);

            var index = 0;
            foreach (var (notification, outgoing) in batch)
            {
                var tickets = outgoing.Select(_ => result.Succeeded ? result.Tickets[index++] : null).ToList();
                Apply(notification, outgoing, tickets, result.Error, now, deliveries);
            }

            batch.Clear();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private void Apply(
        NotificationEntity notification,
        List<Outgoing> outgoing,
        List<ExpoTicket?> tickets,
        string? callError,
        DateTimeOffset now,
        List<PushDelivery> deliveries)
    {
        if (callError is not null)
        {
            deliveries.AddRange(outgoing.Select(target => new PushDelivery(notification.Id, target.Device.Id, target.Device.ExpoPushToken, "unreachable", null, callError)));
            RegisterFailedAttempt(notification, callError);

            return;
        }

        var accepted = new List<string>();
        var okCount = 0;
        string? firstError = null;
        var deviceGone = 0;

        for (var position = 0; position < outgoing.Count; position++)
        {
            var device = outgoing[position].Device;
            var ticket = tickets[position]!;

            if (ticket.IsOk)
            {
                okCount++;

                if (ticket.Id is not null)
                {
                    accepted.Add(ticket.Id);
                }

                deliveries.Add(new PushDelivery(notification.Id, device.Id, device.ExpoPushToken, "ok", ticket.Id, null));
                continue;
            }

            var error = ticket.Error ?? "Error";
            deliveries.Add(new PushDelivery(notification.Id, device.Id, device.ExpoPushToken, "error", null, ticket.Message is null ? error : $"{error}: {ticket.Message}"));

            if (error == Device.DeviceNotRegistered)
            {
                device.Deactivate(Device.DeviceNotRegistered, now);
                deviceGone++;
            }
            else
            {
                firstError ??= ticket.Message is null ? error : $"{error}: {ticket.Message}";
            }
        }

        if (okCount > 0)
        {
            notification.Status = NotificationStatus.Sent;
            notification.SentAt = now;
            notification.FailureReason = null;
            notification.ExpoTicketId = accepted.Count == 0 ? null : JoinTicketIds(accepted);
        }
        else if (firstError is null && deviceGone > 0)
        {
            // The phone is gone, not the notification: keep it for the in-app feed, like one that never had a device.
            notification.Status = NotificationStatus.Suppressed;
            notification.FailureReason = Device.DeviceNotRegistered;
        }
        else
        {
            notification.Status = NotificationStatus.Failed;
            notification.FailureReason = Copy.Clip(firstError ?? "Unknown error", 300);
        }
    }

    private void RegisterFailedAttempt(NotificationEntity notification, string error)
    {
        notification.AttemptCount++;
        notification.FailureReason = Copy.Clip($"ExpoUnavailable: {error}", 300);

        if (notification.AttemptCount >= NotificationEntity.MaxAttempts)
        {
            notification.Status = NotificationStatus.Failed;
            LogGaveUp(logger, notification.Id, notification.AttemptCount);
        }
    }

    /// <summary>The ticket IDs that fit the 100-character column, comma-separated (a UUID ticket is 36 characters, so two devices fit).</summary>
    internal static string JoinTicketIds(IEnumerable<string> ticketIds)
    {
        var joined = string.Empty;

        foreach (var id in ticketIds)
        {
            var next = joined.Length == 0 ? id : $"{joined},{id}";

            if (next.Length > 100)
            {
                break;
            }

            joined = next;
        }

        return joined;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push sender: {Notifications} notification(s), {Messages} message(s) processed.")]
    private static partial void LogRun(ILogger logger, int notifications, int messages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} failed for good after {Attempts} attempts to reach Expo.")]
    private static partial void LogGaveUp(ILogger logger, long notificationId, int attempts);
}
