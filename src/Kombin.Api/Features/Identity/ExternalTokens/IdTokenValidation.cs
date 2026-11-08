using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kombin.Api.Features.Identity.ExternalTokens;

/// <summary>
/// The checks every OpenID Connect ID token goes through, whoever issued it: RS256 signature by one of the provider's JWKS keys, issuer,
/// audience, expiry and not-before against <see cref="IClock"/> with <see cref="ClockSkew"/>, a subject, and the nonce when the app sent
/// one. Any failure is <see cref="IdentityErrors.ExternalTokenInvalid"/>; keys that could never be fetched are
/// <see cref="IdentityErrors.ExternalProviderUnavailable"/>.
/// </summary>
internal static class IdTokenValidation
{
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    public static async Task<Result<JsonWebToken>> ValidateAsync(
        string idToken,
        JwksKeySource keySource,
        IReadOnlyCollection<string> issuers,
        IReadOnlyCollection<string> audiences,
        string? nonce,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!Handler.CanReadToken(idToken))
        {
            return IdentityErrors.ExternalTokenInvalid;
        }

        string? keyId;

        try
        {
            keyId = Handler.ReadJsonWebToken(idToken).Kid;
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
        {
            return IdentityErrors.ExternalTokenInvalid;
        }

        var keys = await keySource.GetKeysAsync(keyId, cancellationToken);

        if (keys is null)
        {
            return IdentityErrors.ExternalProviderUnavailable;
        }

        var now = clock.UtcNow;
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = issuers,
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = ClockSkew,
            // Our clock rather than the machine's, so tests can pin time; a custom validator must enforce "exp is required" itself.
            LifetimeValidator = (notBefore, expires, _, _) => IsWithinLifetime(notBefore, expires, now),
        };

        var result = await Handler.ValidateTokenAsync(idToken, parameters);

        if (!result.IsValid || result.SecurityToken is not JsonWebToken token || string.IsNullOrWhiteSpace(token.Subject))
        {
            return IdentityErrors.ExternalTokenInvalid;
        }

        if (nonce is not null && !NonceMatches(ReadString(token, "nonce"), nonce))
        {
            return IdentityErrors.ExternalTokenInvalid;
        }

        return token;
    }

    internal static bool IsWithinLifetime(DateTime? notBefore, DateTime? expires, DateTimeOffset now) =>
        expires is not null
        && now.UtcDateTime < expires.Value.ToUniversalTime() + ClockSkew
        && (notBefore is null || notBefore.Value.ToUniversalTime() - ClockSkew <= now.UtcDateTime);

    /// <summary>The raw nonce itself (Google) or its SHA-256 as lowercase hex (Apple, which is given the hash by the app).</summary>
    internal static bool NonceMatches(string? claim, string nonce) =>
        claim is not null
        && (string.Equals(claim, nonce, StringComparison.Ordinal)
            || string.Equals(claim, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))), StringComparison.Ordinal));

    public static string? ReadString(JsonWebToken token, string claimType) =>
        token.TryGetClaim(claimType, out var claim) && !string.IsNullOrWhiteSpace(claim.Value) ? claim.Value : null;

    /// <summary>
    /// A boolean claim sent as a JSON boolean (Google) or as the string <c>"true"</c>/<c>"false"</c> (Apple); null when absent or neither.
    /// </summary>
    public static bool? ReadBoolean(JsonWebToken token, string claimType) =>
        token.TryGetClaim(claimType, out Claim claim) && bool.TryParse(claim.Value, out var value) ? value : null;

    public static string? ReadEmail(JsonWebToken token) => ReadString(token, "email")?.Trim().ToLowerInvariant();
}
