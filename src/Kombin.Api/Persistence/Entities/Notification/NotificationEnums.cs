namespace Kombin.Api.Persistence.Entities.Notification;

internal enum DevicePlatform
{
    Ios,
    Android,
}

internal enum NotificationKind
{
    PriceDrop,
    BackInStock,
    AnyChange,
    NewMatches,
    System,
}

internal enum NotificationStatus
{
    /// <summary>Waiting for <see cref="Notification.ScheduledFor"/> or for the push sender.</summary>
    Pending,
    Sent,
    Failed,

    /// <summary>Never pushed (no usable device); it still shows in the in-app feed.</summary>
    Suppressed,
}
