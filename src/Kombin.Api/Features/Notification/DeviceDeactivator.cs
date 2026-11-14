using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification;

/// <summary>The things other features (Identity's logout and account deletion) may ask of Notification.</summary>
internal interface IDeviceDeactivator
{
    /// <summary>Deactivates the caller's own active device with this token. An unknown or foreign token is a silent no-op.</summary>
    Task DeactivateAsync(Guid userId, string expoPushToken, CancellationToken cancellationToken);

    /// <summary>Deactivates every active device of the user (account deletion: nothing is pushed to a pending-deletion account).</summary>
    Task<int> DeactivateAllAsync(Guid userId, string reason, CancellationToken cancellationToken);
}

internal sealed class DeviceDeactivator(KombinDbContext db, IClock clock) : IDeviceDeactivator
{
    public async Task DeactivateAsync(Guid userId, string expoPushToken, CancellationToken cancellationToken)
    {
        var token = expoPushToken.Trim();
        var now = clock.UtcNow;

        await db.Devices
            .Where(device => device.UserId == userId && device.ExpoPushToken == token && device.IsActive)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(device => device.IsActive, false)
                    .SetProperty(device => device.DeactivatedAt, now)
                    .SetProperty(device => device.DeactivationReason, Device.UserRemoved)
                    .SetProperty(device => device.UpdatedAt, now),
                cancellationToken);
    }

    public Task<int> DeactivateAllAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        return db.Devices
            .Where(device => device.UserId == userId && device.IsActive)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(device => device.IsActive, false)
                    .SetProperty(device => device.DeactivatedAt, now)
                    .SetProperty(device => device.DeactivationReason, reason)
                    .SetProperty(device => device.UpdatedAt, now),
                cancellationToken);
    }
}
