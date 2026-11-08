using Kombin.Api.Common.Results;
using Kombin.Api.Persistence.Entities.Identity;

namespace Kombin.Api.Features.Identity.ExternalTokens;

/// <summary>
/// Who the provider says the token belongs to, read from the verified token's claims only (never from the request body).
/// <c>Subject</c> is the provider's stable user ID; <c>Email</c> is lower-cased and trimmed.
/// </summary>
internal sealed record ExternalIdentity(string Provider, string Subject, string? Email, bool EmailVerified, string? Name);

/// <summary>Checks an ID token a phone obtained from Google or Apple. Tests replace it with a fake.</summary>
internal interface IExternalTokenVerifier
{
    /// <summary>
    /// <paramref name="nonce"/> is the raw nonce the app generated, when it used one; the token's <c>nonce</c> claim must then be that
    /// value or its SHA-256 (lowercase hex), which is what the app hands to Apple.
    /// </summary>
    Task<Result<ExternalIdentity>> VerifyAsync(string provider, string idToken, string? nonce, CancellationToken cancellationToken);
}

/// <summary>Picks the provider's verifier by name. The request validator only lets <see cref="ExternalProviders.All"/> through.</summary>
internal sealed class ExternalTokenVerifier(GoogleTokenVerifier google, AppleTokenVerifier apple) : IExternalTokenVerifier
{
    public Task<Result<ExternalIdentity>> VerifyAsync(string provider, string idToken, string? nonce, CancellationToken cancellationToken) =>
        provider switch
        {
            ExternalProviders.Google => google.VerifyAsync(idToken, nonce, cancellationToken),
            ExternalProviders.Apple => apple.VerifyAsync(idToken, nonce, cancellationToken),
            _ => Task.FromResult<Result<ExternalIdentity>>(IdentityErrors.ExternalTokenInvalid),
        };
}
