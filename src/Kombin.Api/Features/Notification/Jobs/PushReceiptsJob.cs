using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.Features.Notification.Jobs;

/// <summary>
/// Asks Expo what became of the pushes it accepted. Expo has receipts ready roughly 15 minutes after sending and keeps them for a day,
/// so only notifications sent between 15 minutes and 24 hours ago are checked, each once (<see cref="NotificationEntity.ReceiptCheckedAt"/>).
/// A receipt that is not ready yet is simply asked for again on the next run.
/// </summary>
internal sealed partial class PushReceiptsJob(KombinDbContext db, IExpoPushClient expo, IClock clock, ILogger<PushReceiptsJob> logger)
{
    public const string JobId = "push-receipts";

    public const int BatchSize = 300;

    public const int MaxNotificationsPerRun = 1000;

    public static readonly TimeSpan MinAge = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var checkedCount = await CheckAsync(null, cancellationToken);

        if (checkedCount > 0)
        {
            LogChecked(logger, checkedCount);
        }
    }

    /// <summary>Returns how many notifications had all their receipts and were marked as checked.</summary>
    public async Task<int> CheckAsync(Guid? userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var newest = now - MinAge;
        var oldest = now - MaxAge;

        var sent = db.Notifications.Where(notification =>
            notification.Status == NotificationStatus.Sent
            && notification.ExpoTicketId != null
            && notification.ReceiptCheckedAt == null
            && notification.SentAt >= oldest
            && notification.SentAt <= newest);

        if (userId is { } onlyUser)
        {
            sent = sent.Where(notification => notification.UserId == onlyUser);
        }

        var notifications = await sent.OrderBy(notification => notification.SentAt).Take(MaxNotificationsPerRun).ToListAsync(cancellationToken);
        if (notifications.Count == 0)
        {
            return 0;
        }

        var ticketsByNotification = notifications.ToDictionary(
            notification => notification.Id,
            notification => notification.ExpoTicketId!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var receipts = new Dictionary<string, ExpoReceipt>(StringComparer.Ordinal);
        foreach (var chunk in ticketsByNotification.Values.SelectMany(ids => ids).Distinct(StringComparer.Ordinal).Chunk(BatchSize))
        {
            var result = await expo.GetReceiptsAsync(chunk, cancellationToken);

            // A failed call is retried by the next run; whatever was already fetched is still used.
            foreach (var (ticketId, receipt) in result.Receipts)
            {
                receipts[ticketId] = receipt;
            }
        }

        var userIds = notifications.Select(notification => notification.UserId).Distinct().ToList();
        var devices = (await db.Devices
            .Where(device => device.IsActive && userIds.Contains(device.UserId))
            .ToListAsync(cancellationToken)).ToLookup(device => device.UserId);

        var finished = 0;

        foreach (var notification in notifications)
        {
            var ticketIds = ticketsByNotification[notification.Id];

            if (!ticketIds.All(receipts.ContainsKey))
            {
                continue;
            }

            Apply(notification, ticketIds.Select(id => receipts[id]).ToList(), devices[notification.UserId].ToList(), now);
            finished++;
        }

        await db.SaveChangesAsync(cancellationToken);

        return finished;
    }

    private static void Apply(NotificationEntity notification, List<ExpoReceipt> receipts, List<Device> userDevices, DateTimeOffset now)
    {
        notification.ReceiptCheckedAt = now;

        var failed = receipts.Where(receipt => !receipt.IsOk).ToList();
        if (failed.Count == 0)
        {
            return;
        }

        foreach (var receipt in failed.Where(receipt => receipt.Error == Device.DeviceNotRegistered))
        {
            ResolveDevice(userDevices, receipt, notification)?.Deactivate(Device.DeviceNotRegistered, now);
        }

        // One device failing does not matter while another got the push; only a notification that reached nobody is marked.
        if (failed.Count < receipts.Count)
        {
            return;
        }

        var other = failed.FirstOrDefault(receipt => receipt.Error != Device.DeviceNotRegistered);
        if (other is null)
        {
            notification.Status = NotificationStatus.Suppressed;
            notification.FailureReason = Device.DeviceNotRegistered;
        }
        else
        {
            var error = other.Error ?? "Error";
            notification.Status = NotificationStatus.Failed;
            notification.FailureReason = Copy.Clip(other.Message is null ? error : $"{error}: {other.Message}", 300);
        }
    }

    /// <summary>
    /// A receipt names no device, so the device is found by the token in Expo's message, or, failing that, as the user's only device
    /// that already existed when the push went out. Anything less certain deactivates nothing: the next send gets an error ticket
    /// that is tied to its device.
    /// </summary>
    private static Device? ResolveDevice(List<Device> userDevices, ExpoReceipt receipt, NotificationEntity notification)
    {
        if (receipt.Message is { } message && userDevices.FirstOrDefault(device => message.Contains(device.ExpoPushToken, StringComparison.Ordinal)) is { } named)
        {
            return named;
        }

        var candidates = userDevices.Where(device => device.IsActive && device.CreatedAt <= notification.SentAt).ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push receipts: {Count} notification(s) checked.")]
    private static partial void LogChecked(ILogger logger, int count);
}
