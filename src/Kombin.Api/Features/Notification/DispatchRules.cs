using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification;

internal enum DispatchSkip
{
    None,
    PreferenceOff,
    Duplicate,
}

/// <summary>What to do with one alert trigger. A skipped trigger gets no notification row; either way the trigger is consumed.</summary>
internal sealed record DispatchDecision(DispatchSkip Skip, NotificationStatus Status, string? FailureReason, DateTimeOffset ScheduledFor)
{
    public bool Create => Skip == DispatchSkip.None;
}

/// <summary>The rules of the alert dispatcher without any I/O; the job loads the facts and applies the decision.</summary>
internal static class DispatchRules
{
    /// <summary>The window in which a notification with the same dedupe key is not sent again.</summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// In order: kind switched off in the preferences → skip; same dedupe key within the window → skip; no active device →
    /// a <see cref="NotificationStatus.Suppressed"/> row for the in-app feed; otherwise a <see cref="NotificationStatus.Pending"/> row
    /// scheduled now, or at the end of the quiet hours when <paramref name="now"/> falls inside them.
    /// </summary>
    public static DispatchDecision Decide(
        NotificationKind kind,
        NotificationPreference? preference,
        bool hasActiveDevice,
        bool hasRecentDuplicate,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        if (!NotificationKinds.IsEnabled(preference, kind))
        {
            return new DispatchDecision(DispatchSkip.PreferenceOff, NotificationStatus.Suppressed, null, now);
        }

        if (hasRecentDuplicate)
        {
            return new DispatchDecision(DispatchSkip.Duplicate, NotificationStatus.Suppressed, null, now);
        }

        if (!hasActiveDevice)
        {
            return new DispatchDecision(DispatchSkip.None, NotificationStatus.Suppressed, Persistence.Entities.Notification.Notification.NoDevice, now);
        }

        return new DispatchDecision(
            DispatchSkip.None,
            NotificationStatus.Pending,
            null,
            QuietHours.NextAllowed(now, preference?.QuietHoursStart, preference?.QuietHoursEnd, zone));
    }
}
