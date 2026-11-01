namespace Kombin.Api.Persistence.Entities.Notification;

/// <summary>
/// An Expo push token of a user's phone. A token is active for exactly one user at a time: registering it under another user
/// deactivates the old row (<see cref="Replaced"/>) and adds a new one. Rows are never deleted, so history survives.
/// </summary>
internal sealed class Device : BaseEntity
{
    public const string DeviceNotRegistered = "DeviceNotRegistered";

    public const string UserRemoved = "UserRemoved";

    public const string Replaced = "Replaced";

    public Guid UserId { get; set; }

    public string ExpoPushToken { get; set; } = string.Empty;

    public DevicePlatform Platform { get; set; }

    public string? AppVersion { get; set; }

    public string? DeviceName { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? DeactivatedAt { get; set; }

    /// <summary>One of <see cref="DeviceNotRegistered"/>, <see cref="UserRemoved"/> and <see cref="Replaced"/>.</summary>
    public string? DeactivationReason { get; set; }

    public void Deactivate(string reason, DateTimeOffset now)
    {
        IsActive = false;
        DeactivatedAt = now;
        DeactivationReason = reason;
    }
}
