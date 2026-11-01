using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Identity.RefreshToken;

internal sealed record RefreshRequest(string RefreshToken);

internal static class RefreshTokenEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/refresh", Handle)
            .WithName("RefreshToken")
            .Produces<AuthTokensResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(
        RefreshRequest request,
        KombinDbContext db,
        AuthTokenIssuer tokens,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return IdentityErrors.InvalidRefreshToken.ToHttpResult();
        }

        var tokenHash = JwtTokenService.HashRefreshToken(request.RefreshToken);
        var current = await db.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

        if (current is null)
        {
            return IdentityErrors.InvalidRefreshToken.ToHttpResult();
        }

        var now = clock.UtcNow;

        if (current.RevokedAt is not null)
        {
            // A rotated or revoked token is being replayed: assume it leaked and end every session of that user.
            await RevokeActiveTokensAsync(db, current.UserId, now, cancellationToken);
            return IdentityErrors.InvalidRefreshToken.ToHttpResult();
        }

        if (current.ExpiresAt <= now)
        {
            return IdentityErrors.InvalidRefreshToken.ToHttpResult();
        }

        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == current.UserId, cancellationToken);

        if (user is null)
        {
            return IdentityErrors.InvalidRefreshToken.ToHttpResult();
        }

        var issued = tokens.Issue(user, current.DeviceName);
        current.RevokedAt = now;
        current.ReplacedByTokenId = issued.RefreshToken.Id;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request rotated the same token first, which is indistinguishable from a replay.
            await RevokeActiveTokensAsync(db, current.UserId, now, cancellationToken);
            return IdentityErrors.InvalidRefreshToken.ToHttpResult();
        }

        return Results.Ok(issued.Response);
    }

    private static Task<int> RevokeActiveTokensAsync(KombinDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.UpdatedAt, now),
                cancellationToken);
}
