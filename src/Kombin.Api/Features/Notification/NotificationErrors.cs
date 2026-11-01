using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Notification;

internal static class NotificationErrors
{
    // Also the answer for a notification that belongs to someone else: whether it exists is none of the caller's business.
    public static readonly Error NotFound =
        Error.NotFound("Notification.NotFound", "The notification does not exist.");

    public static readonly Error InvalidPushToken =
        Error.Validation("Notification.InvalidPushToken", "The push token must look like ExponentPushToken[...] or ExpoPushToken[...].");

    public static readonly Error QuietHoursIncomplete =
        Error.Validation("Notification.QuietHoursIncomplete", "Quiet hours need both a start and an end, or neither.");

    // Internal test endpoint only.
    public static readonly Error UserNotFound =
        Error.NotFound("Notification.UserNotFound", "The user does not exist.");
}
