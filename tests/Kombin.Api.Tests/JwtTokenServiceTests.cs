using System.Security.Cryptography;
using Kombin.Api.Common.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kombin.Api.Tests;

public class JwtTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly JwtOptions _options = new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = "unit-test-signing-key-0123456789-abcdef",
        AccessTokenLifetime = TimeSpan.FromMinutes(15),
        RefreshTokenLifetime = TimeSpan.FromDays(30),
    };

    private JwtTokenService CreateService() => new(Options.Create(_options), new FixedClock(Now));

    [Fact]
    public async Task AccessTokenContainsSubjectEmailAndExpiry()
    {
        var userId = Guid.NewGuid();

        var accessToken = CreateService().CreateAccessToken(userId, "ada@example.com");
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken.Token, ValidationParameters(validateLifetime: false));

        Assert.True(result.IsValid);
        var token = (JsonWebToken)result.SecurityToken;
        Assert.Equal(userId.ToString(), token.Subject);
        Assert.Equal("ada@example.com", token.GetClaim(JwtRegisteredClaimNames.Email).Value);
        Assert.Equal(Now.AddMinutes(15), accessToken.ExpiresAt);
        Assert.Equal(Now.AddMinutes(15).UtcDateTime, token.ValidTo);
        Assert.Equal(SecurityAlgorithms.HmacSha256, token.Alg);
    }

    [Fact]
    public async Task TokenSignedWithAnotherKeyIsRejected()
    {
        var accessToken = CreateService().CreateAccessToken(Guid.NewGuid(), "ada@example.com");
        var parameters = ValidationParameters(validateLifetime: false);
        parameters.IssuerSigningKey = JwtTokenService.CreateSigningKey("a-completely-different-signing-key-9876543210");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken.Token, parameters);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        // Issued in 2026-01-01, validated against the real clock, so it is long expired.
        var accessToken = CreateService().CreateAccessToken(Guid.NewGuid(), "ada@example.com");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken.Token, ValidationParameters(validateLifetime: true));

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenExpiredException>(result.Exception);
    }

    [Fact]
    public void RefreshTokensAreUniqueAndUrlSafe()
    {
        var first = JwtTokenService.GenerateRefreshToken();
        var second = JwtTokenService.GenerateRefreshToken();

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length); // 32 random bytes, base64url without padding
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('=', first);
    }

    [Fact]
    public void RefreshTokenHashIsDeterministicSha256Hex()
    {
        var hash = JwtTokenService.HashRefreshToken("opaque-token");

        Assert.Equal(hash, JwtTokenService.HashRefreshToken("opaque-token"));
        Assert.NotEqual(hash, JwtTokenService.HashRefreshToken("opaque-token-2"));
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("opaque-token"u8)), hash);
        Assert.DoesNotContain("opaque-token", hash, StringComparison.Ordinal);
    }

    private TokenValidationParameters ValidationParameters(bool validateLifetime) => new()
    {
        ValidIssuer = _options.Issuer,
        ValidAudience = _options.Audience,
        IssuerSigningKey = JwtTokenService.CreateSigningKey(_options.SigningKey),
        ValidateLifetime = validateLifetime,
        ClockSkew = TimeSpan.Zero,
    };
}
