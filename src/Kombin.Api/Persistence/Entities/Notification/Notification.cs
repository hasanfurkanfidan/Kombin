using Kombin.Api.Persistence.Entities.Engagement;

namespace Kombin.Api.Persistence.Entities.Notification;

/// <summary>
/// One thing to tell a user. The row is both the push to deliver and the entry of the in-app feed; a user with two devices
/// still has one row. Not a <see cref="BaseEntity"/>: the identity is a bigint and the timestamps are set by the creator.
/// </summary>
internal sealed class Notification
{
    public const string NoDevice = "NoDevice";

    public const int MaxAttempts = 5;

    public long Id { get; set; }

    public Guid UserId { get; set; }

    public NotificationKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>A JSON object: <c>productId?, offerId?, personaId?, alertId?, productIds?, deepLink</c>.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>Notifications of one user with the same key within 24 hours are collapsed into the first.</summary>
    public string DedupeKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Now, or the end of the user's quiet hours.</summary>
    public DateTimeOffset ScheduledFor { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    public NotificationStatus Status { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>The Expo ticket IDs of the messages sent for this row, comma-separated (one per device).</summary>
    public string? ExpoTicketId { get; set; }

    /// <summary>Failed attempts to reach the Expo push service; the row fails for good at <see cref="MaxAttempts"/>.</summary>
    public int AttemptCount { get; set; }

    public DateTimeOffset? ReceiptCheckedAt { get; set; }

    /// <summary>The alert trigger this notification was made from; null for other kinds and after the trigger is deleted.</summary>
    public long? AlertTriggerId { get; set; }

    public AlertTrigger? AlertTrigger { get; set; }
}
