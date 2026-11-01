using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification;

/// <summary>The one thing other features (Identity's logout) may ask of Notification.</summary>
internal interface IDeviceDeactivator
{
    /// <summary>Deactivates the caller's own active device with this token. An unknown or foreign token is a silent no-op.</summary>
    Task DeactivateAsync(Guid userId, string expoPushToken, CancellationToken cancellationToken);
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
}
