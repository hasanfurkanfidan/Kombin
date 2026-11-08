using Kombin.Api.Common.Results;
using Kombin.Api.Features.Identity;
using Kombin.Api.Features.Identity.ExternalTokens;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Stands in for Google and Apple: the "ID token" is the identity spelled out, <c>sub=...;email=...;verified=true;name=...</c>
/// (<see cref="Token"/>), so each test mints its own subjects and the shared database stays independent between tests. The tokens
/// <c>invalid</c>, <c>disabled</c> and <c>unavailable</c> answer the matching errors. The real verifiers are unit-tested.
/// </summary>
internal sealed class FakeExternalTokenVerifier : IExternalTokenVerifier
{
    public static string Token(string subject, string? email, bool verified = true, string? name = null) =>
        string.Join(';', new[] { $"sub={subject}", email is null ? null : $"email={email}", $"verified={verified}", name is null ? null : $"name={name}" }
            .Where(part => part is not null));

    public static string NewSubject() => $"sub-{Guid.NewGuid():N}";

    public Task<Result<ExternalIdentity>> VerifyAsync(string provider, string idToken, string? nonce, CancellationToken cancellationToken)
    {
        Result<ExternalIdentity> result = idToken switch
        {
            "invalid" => IdentityErrors.ExternalTokenInvalid,
            "disabled" => IdentityErrors.ExternalProviderDisabled,
            "unavailable" => IdentityErrors.ExternalProviderUnavailable,
            _ => Parse(provider, idToken),
        };

        return Task.FromResult(result);
    }

    private static Result<ExternalIdentity> Parse(string provider, string idToken)
    {
        var parts = idToken.Split(';')
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);

        if (!parts.TryGetValue("sub", out var subject))
        {
            return IdentityErrors.ExternalTokenInvalid;
        }

        var email = parts.GetValueOrDefault("email")?.Trim().ToLowerInvariant();
        var verified = email is not null && bool.Parse(parts.GetValueOrDefault("verified") ?? "true");

        return new ExternalIdentity(provider, subject, email, verified, parts.GetValueOrDefault("name"));
    }
}
