using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Identity;

/// <summary>What disabling or enabling did. <c>RevokedTokens</c> is how many refresh tokens this call ended (0 when enabling).</summary>
internal sealed record AccountAccessResponse(Guid UserId, bool IsDisabled, DateTimeOffset? DisabledAt, int RevokedTokens);

/// <summary>
/// The founder's soft account switch, shared by the internal endpoints and the admin panel. Disabling sets <c>DisabledAt</c> and revokes every
/// refresh token; sign-in, external sign-in and refresh then answer <c>Identity.AccountDisabled</c>. Data is left alone and the switch is
/// reversible. An access token already issued stays valid until it expires (<c>Jwt:AccessTokenLifetime</c>, 15 minutes by default).
/// </summary>
internal sealed class AccountAccess(KombinDbContext db, IClock clock)
{
    public async Task<Result<AccountAccessResponse>> DisableAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var now = clock.UtcNow;
        user.DisabledAt ??= now;
        await db.SaveChangesAsync(cancellationToken);

        // Also on a second call: tokens issued between the two calls (there are none, sign-in is refused) would be ended too.
        var revoked = await db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.UpdatedAt, now),
                cancellationToken);

        return new AccountAccessResponse(userId, true, user.DisabledAt, revoked);
    }

    public async Task<Result<AccountAccessResponse>> EnableAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        if (user.DisabledAt is not null)
        {
            user.DisabledAt = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new AccountAccessResponse(userId, false, null, 0);
    }
}
