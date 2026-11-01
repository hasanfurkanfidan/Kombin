using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Common.Time;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kombin.Api.Common.Auth;

internal sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

internal sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock)
{
    private const int RefreshTokenBytes = 32;

    private static readonly JsonWebTokenHandler TokenHandler = new();

    public AccessToken CreateAccessToken(Guid userId, string email)
    {
        var jwt = options.Value;
        var now = clock.UtcNow;
        var expiresAt = now + jwt.AccessTokenLifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Email] = email,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(CreateSigningKey(jwt.SigningKey), SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(TokenHandler.CreateToken(descriptor), expiresAt);
    }

    /// <summary>Creates a 256-bit opaque token. Only its hash is ever persisted.</summary>
    public static string GenerateRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(RefreshTokenBytes));

    /// <summary>SHA-256 of the opaque token as lowercase hex (64 characters).</summary>
    public static string HashRefreshToken(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    internal static SymmetricSecurityKey CreateSigningKey(string signingKey) => new(Encoding.UTF8.GetBytes(signingKey));
}
