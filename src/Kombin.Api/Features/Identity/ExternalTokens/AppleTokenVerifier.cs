using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Identity.ExternalTokens;

/// <summary>
/// Apple ID tokens (Sign in with Apple): Apple's JWKS, issuer <c>https://appleid.apple.com</c>, audience one of
/// <c>Authentication:Apple:ClientIds</c>. Apple verifies every email it shares, so a missing <c>email_verified</c> counts as verified and
/// only an explicit <c>false</c> (boolean or the string <c>"false"</c>) does not. Private-relay addresses
/// (<c>@privaterelay.appleid.com</c>) are ordinary emails here. Apple never puts the user's name in the token: the app sends it once.
/// </summary>
internal sealed class AppleTokenVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<ExternalAuthOptions> options,
    IClock clock,
    ILogger<AppleTokenVerifier> logger) : IDisposable
{
    internal static readonly Uri JwksUri = new("https://appleid.apple.com/auth/keys");

    internal static readonly IReadOnlyList<string> Issuers = ["https://appleid.apple.com"];

    private readonly JwksKeySource _keys = new(JwksUri, httpClientFactory, clock, logger);

    public void Dispose() => _keys.Dispose();

    public async Task<Result<ExternalIdentity>> VerifyAsync(string idToken, string? nonce, CancellationToken cancellationToken)
    {
        var clientIds = options.Value.Apple.ConfiguredClientIds();

        if (clientIds.Count == 0)
        {
            return IdentityErrors.ExternalProviderDisabled;
        }

        var validated = await IdTokenValidation.ValidateAsync(idToken, _keys, Issuers, clientIds, nonce, clock, cancellationToken);

        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var token = validated.Value;
        var email = IdTokenValidation.ReadEmail(token);
        var emailVerified = email is not null && IdTokenValidation.ReadBoolean(token, "email_verified") != false;

        return new ExternalIdentity(ExternalProviders.Apple, token.Subject, email, emailVerified, IdTokenValidation.ReadString(token, "name"));
    }
}
