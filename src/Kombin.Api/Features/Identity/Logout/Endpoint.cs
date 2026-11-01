using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Notification;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Identity.Logout;

/// <summary><c>ExpoPushToken</c> is the phone's push token; when given, that device stops receiving pushes for this account.</summary>
internal sealed record LogoutRequest(string RefreshToken, string? ExpoPushToken = null);

internal static class LogoutEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/logout", Handle)
            .WithName("Logout")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(
        LogoutRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IDeviceDeactivator deviceDeactivator,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            var tokenHash = JwtTokenService.HashRefreshToken(request.RefreshToken);
            var now = clock.UtcNow;

            // Scoped to the caller's own tokens; an unknown or foreign token is a silent no-op so logout stays idempotent.
            await db.RefreshTokens
                .Where(token => token.TokenHash == tokenHash && token.UserId == userId && token.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(token => token.RevokedAt, now)
                        .SetProperty(token => token.UpdatedAt, now),
                    cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ExpoPushToken))
        {
            await deviceDeactivator.DeactivateAsync(userId, request.ExpoPushToken, cancellationToken);
        }

        return Results.NoContent();
    }
}
