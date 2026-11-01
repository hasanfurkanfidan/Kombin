using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;
using Npgsql;

namespace Kombin.Api.Features.Notification.RegisterDevice;

/// <summary><c>Platform</c> is <c>Ios</c> or <c>Android</c>.</summary>
internal sealed record RegisterDeviceRequest(string ExpoPushToken, string Platform, string? AppVersion, string? DeviceName);

internal static class RegisterDeviceEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPut("/", Handle)
            .WithName("RegisterDevice")
            .WithValidation<RegisterDeviceRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(
        RegisterDeviceRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var token = request.ExpoPushToken.Trim();

        // Not in the validator because the client reads this one by its error code.
        if (!PushTokens.IsValid(token))
        {
            return NotificationErrors.InvalidPushToken.ToHttpResult();
        }

        var userId = currentUser.UserId;
        var platform = Enum.Parse<DevicePlatform>(request.Platform, ignoreCase: true);

        // Two parallel registrations of one token race on the unique index of active tokens; the loser retries and finds the winner's row.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await UpsertAsync(db, userId, token, platform, request, clock.UtcNow, cancellationToken);

                return TypedResults.NoContent();
            }
            catch (DbUpdateException exception) when (attempt == 1 && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task UpsertAsync(
        KombinDbContext db, Guid userId, string token, DevicePlatform platform, RegisterDeviceRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var rows = await db.Devices.Where(device => device.ExpoPushToken == token).ToListAsync(cancellationToken);
        var active = rows.SingleOrDefault(device => device.IsActive);

        Device device;

        if (active is not null && active.UserId == userId)
        {
            device = active;
        }
        else
        {
            if (active is not null)
            {
                // The token moved to another account (logout and login on the same phone, or a shared phone): it is theirs no more.
                active.Deactivate(Device.Replaced, now);
                await db.SaveChangesAsync(cancellationToken);
            }

            device = rows.FirstOrDefault(candidate => candidate.UserId == userId) ?? new Device { UserId = userId, ExpoPushToken = token };

            if (db.Entry(device).State == EntityState.Detached)
            {
                db.Devices.Add(device);
            }

            device.IsActive = true;
            device.DeactivatedAt = null;
            device.DeactivationReason = null;
        }

        device.Platform = platform;
        device.AppVersion = request.AppVersion?.Trim();
        device.DeviceName = request.DeviceName?.Trim();
        device.LastSeenAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
