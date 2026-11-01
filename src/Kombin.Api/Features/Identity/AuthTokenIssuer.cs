using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.Extensions.Options;
using RefreshTokenEntity = Kombin.Api.Persistence.Entities.Identity.RefreshToken;

namespace Kombin.Api.Features.Identity;

internal sealed record AuthTokensResponse(string AccessToken, string RefreshToken, int ExpiresIn);

internal sealed record IssuedTokens(AuthTokensResponse Response, RefreshTokenEntity RefreshToken);

/// <summary>Shared by register, login and refresh: mints an access token and stages a new refresh token for saving.</summary>
internal sealed class AuthTokenIssuer(KombinDbContext db, JwtTokenService jwt, IOptions<JwtOptions> options, IClock clock)
{
    public IssuedTokens Issue(User user, string? deviceName)
    {
        var accessToken = jwt.CreateAccessToken(user.Id, user.Email);
        var rawRefreshToken = JwtTokenService.GenerateRefreshToken();

        var refreshToken = new RefreshTokenEntity
        {
            UserId = user.Id,
            TokenHash = JwtTokenService.HashRefreshToken(rawRefreshToken),
            ExpiresAt = clock.UtcNow + options.Value.RefreshTokenLifetime,
            DeviceName = deviceName,
        };

        db.RefreshTokens.Add(refreshToken);

        var expiresIn = (int)options.Value.AccessTokenLifetime.TotalSeconds;

        return new IssuedTokens(new AuthTokensResponse(accessToken.Token, rawRefreshToken, expiresIn), refreshToken);
    }
}
