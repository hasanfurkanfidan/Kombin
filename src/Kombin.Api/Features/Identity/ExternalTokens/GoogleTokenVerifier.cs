using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Identity.ExternalTokens;

/// <summary>
/// Google ID tokens (Sign in with Google on Android, iOS and web): Google's JWKS, issuer <c>https://accounts.google.com</c> or
/// <c>accounts.google.com</c>, audience one of <c>Authentication:Google:ClientIds</c>. A token must carry an email with
/// <c>email_verified = true</c>, which is what makes linking an existing account by email safe.
/// </summary>
internal sealed class GoogleTokenVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<ExternalAuthOptions> options,
    IClock clock,
    ILogger<GoogleTokenVerifier> logger) : IDisposable
{
    internal static readonly Uri JwksUri = new("https://www.googleapis.com/oauth2/v3/certs");

    internal static readonly IReadOnlyList<string> Issuers = ["https://accounts.google.com", "accounts.google.com"];

    private readonly JwksKeySource _keys = new(JwksUri, httpClientFactory, clock, logger);

    public void Dispose() => _keys.Dispose();

    public async Task<Result<ExternalIdentity>> VerifyAsync(string idToken, string? nonce, CancellationToken cancellationToken)
    {
        var clientIds = options.Value.Google.ConfiguredClientIds();

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

        if (email is null || IdTokenValidation.ReadBoolean(token, "email_verified") != true)
        {
            return IdentityErrors.ExternalEmailMissing;
        }

        return new ExternalIdentity(ExternalProviders.Google, token.Subject, email, EmailVerified: true, IdTokenValidation.ReadString(token, "name"));
    }
}
