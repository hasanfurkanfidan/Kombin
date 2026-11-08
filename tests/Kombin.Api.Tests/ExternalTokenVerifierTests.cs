using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kombin.Api.Features.Identity;
using Kombin.Api.Features.Identity.ExternalTokens;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kombin.Api.Tests;

/// <summary>The real Google and Apple verifiers against tokens signed with test RSA keys and a fake JWKS endpoint.</summary>
public sealed class ExternalTokenVerifierTests : IDisposable
{
    private const string GoogleClientId = "123-android.apps.googleusercontent.com";
    private const string AppleClientId = "app.kombin.ios";

    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private readonly FixedClock _clock = new(Now);
    private readonly FakeJwksHandler _jwks = new();
    private readonly RSA _rsaA = RSA.Create(2048);
    private readonly RSA _rsaB = RSA.Create(2048);
    private readonly RsaSecurityKey _keyA;
    private readonly RsaSecurityKey _keyB;
    private readonly ExternalAuthOptions _options = new()
    {
        Google = { ClientIds = ["other-web-client", GoogleClientId] },
        Apple = { ClientIds = [AppleClientId] },
    };

    public ExternalTokenVerifierTests()
    {
        _keyA = new RsaSecurityKey(_rsaA) { KeyId = "key-a" };
        _keyB = new RsaSecurityKey(_rsaB) { KeyId = "key-b" };
        _jwks.Publish(_keyA);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _rsaA.Dispose();
        _rsaB.Dispose();
        _jwks.Dispose();
    }

    private GoogleTokenVerifier Google() =>
        new(new FakeHttpClientFactory(_jwks), Options.Create(_options), _clock, NullLogger<GoogleTokenVerifier>.Instance);

    private AppleTokenVerifier Apple() =>
        new(new FakeHttpClientFactory(_jwks), Options.Create(_options), _clock, NullLogger<AppleTokenVerifier>.Instance);

    private static string Token(
        RsaSecurityKey key,
        string issuer = "https://accounts.google.com",
        string audience = GoogleClientId,
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null,
        IDictionary<string, object>? claims = null,
        string subject = "1098765432101234567890")
    {
        var payload = new Dictionary<string, object> { ["sub"] = subject };

        foreach (var claim in claims ?? new Dictionary<string, object> { ["email"] = "Ada@Example.com", ["email_verified"] = true, ["name"] = "Ada Lovelace" })
        {
            payload[claim.Key] = claim.Value;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = Now.AddMinutes(-1).UtcDateTime,
            NotBefore = (notBefore ?? Now.AddMinutes(-1)).UtcDateTime,
            Expires = (expires ?? Now.AddMinutes(59)).UtcDateTime,
            Claims = payload,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    private static string AppleToken(RsaSecurityKey key, IDictionary<string, object> claims) =>
        Token(key, issuer: "https://appleid.apple.com", audience: AppleClientId, claims: claims, subject: "001234.abcdef0123456789.1234");

    [Fact]
    public async Task AValidGoogleTokenYieldsItsIdentity()
    {
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA), nonce: null, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ExternalIdentity(ExternalProviders.Google, "1098765432101234567890", "ada@example.com", true, "Ada Lovelace"), result.Value);
    }

    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("accounts.google.com")]
    public async Task BothGoogleIssuerSpellingsAreAccepted(string issuer)
    {
        using var google = Google();

        Assert.True((await google.VerifyAsync(Token(_keyA, issuer: issuer), null, Ct)).IsSuccess);
    }

    [Fact]
    public async Task AnExpiredTokenIsInvalid()
    {
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA, expires: Now.AddMinutes(-3)), null, Ct);

        Assert.Equal(IdentityErrors.ExternalTokenInvalid, result.Error);
    }

    [Fact]
    public async Task TwoMinutesOfClockSkewAreTolerated()
    {
        using var google = Google();

        Assert.True((await google.VerifyAsync(Token(_keyA, expires: Now.AddSeconds(-90)), null, Ct)).IsSuccess);
        Assert.True((await google.VerifyAsync(Token(_keyA, notBefore: Now.AddSeconds(90)), null, Ct)).IsSuccess);
        Assert.Equal(IdentityErrors.ExternalTokenInvalid, (await google.VerifyAsync(Token(_keyA, notBefore: Now.AddMinutes(3)), null, Ct)).Error);
    }

    [Fact]
    public async Task AWrongAudienceIsInvalid()
    {
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA, audience: "someone-elses-app.apps.googleusercontent.com"), null, Ct);

        Assert.Equal(IdentityErrors.ExternalTokenInvalid, result.Error);
    }

    [Fact]
    public async Task AWrongIssuerIsInvalid()
    {
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA, issuer: "https://evil.example.com"), null, Ct);

        Assert.Equal(IdentityErrors.ExternalTokenInvalid, result.Error);
    }

    [Fact]
    public async Task AnAppleTokenIsNotAGoogleToken()
    {
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA, issuer: "https://appleid.apple.com"), null, Ct);

        Assert.Equal(IdentityErrors.ExternalTokenInvalid, result.Error);
    }

    [Fact]
    public async Task ATokenSignedByAnotherKeyUnderAKnownKeyIdIsInvalid()
    {
        using var forger = RSA.Create(2048);
        using var google = Google();

        var result = await google.VerifyAsync(Token(new RsaSecurityKey(forger) { KeyId = "key-a" }), null, Ct);

        Assert.Equal(IdentityErrors.ExternalTokenInvalid, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiIxIn0.")]
    public async Task GarbageIsInvalid(string idToken)
    {
        using var google = Google();

        Assert.Equal(IdentityErrors.ExternalTokenInvalid, (await google.VerifyAsync(idToken, null, Ct)).Error);
    }

    [Fact]
    public async Task KeysAreFetchedOnceAndCachedForADay()
    {
        using var google = Google();

        await google.VerifyAsync(Token(_keyA), null, Ct);
        await google.VerifyAsync(Token(_keyA), null, Ct);
        Assert.Equal(1, _jwks.Requests);

        _clock.UtcNow = Now.AddHours(24);
        Assert.True((await google.VerifyAsync(Token(_keyA, expires: Now.AddHours(25)), null, Ct)).IsSuccess);
        Assert.Equal(2, _jwks.Requests);
    }

    [Fact]
    public async Task AnUnknownKeyIdRefreshesTheKeysOnce()
    {
        using var google = Google();
        Assert.True((await google.VerifyAsync(Token(_keyA), null, Ct)).IsSuccess);

        // The provider rotates: key B appears after our fetch.
        _clock.UtcNow = Now.AddMinutes(5);
        _jwks.Publish(_keyA, _keyB);

        Assert.True((await google.VerifyAsync(Token(_keyB), null, Ct)).IsSuccess);
        Assert.Equal(2, _jwks.Requests);
    }

    [Fact]
    public async Task AKeyIdTheProviderNeverPublishedCostsOneRefreshAndFails()
    {
        using var google = Google();
        await google.VerifyAsync(Token(_keyA), null, Ct);
        _clock.UtcNow = Now.AddMinutes(5);

        var result = await google.VerifyAsync(Token(_keyB), null, Ct);

        Assert.Equal(IdentityErrors.ExternalTokenInvalid, result.Error);
        Assert.Equal(2, _jwks.Requests);
    }

    [Fact]
    public async Task UnknownKeyIdsDoNotRefreshMoreThanOnceAMinute()
    {
        using var google = Google();
        await google.VerifyAsync(Token(_keyA), null, Ct);

        await google.VerifyAsync(Token(_keyB), null, Ct);
        await google.VerifyAsync(Token(_keyB), null, Ct);

        Assert.Equal(1, _jwks.Requests);
    }

    [Fact]
    public async Task KeysThatCannotBeFetchedMakeTheProviderUnavailable()
    {
        _jwks.Fail = true;
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA), null, Ct);

        Assert.Equal(IdentityErrors.ExternalProviderUnavailable, result.Error);
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheCachedKeys()
    {
        using var google = Google();
        await google.VerifyAsync(Token(_keyA), null, Ct);
        _clock.UtcNow = Now.AddHours(25);
        _jwks.Fail = true;

        var result = await google.VerifyAsync(Token(_keyA, expires: Now.AddHours(26)), null, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _jwks.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData("true")]
    public async Task GoogleEmailVerifiedMayBeABooleanOrAString(object emailVerified)
    {
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA, claims: new Dictionary<string, object> { ["email"] = "ada@example.com", ["email_verified"] = emailVerified }), null, Ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.EmailVerified);
        Assert.Null(result.Value.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData("false")]
    public async Task AGoogleTokenWithAnUnverifiedEmailIsRefused(object emailVerified)
    {
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA, claims: new Dictionary<string, object> { ["email"] = "ada@example.com", ["email_verified"] = emailVerified }), null, Ct);

        Assert.Equal(IdentityErrors.ExternalEmailMissing, result.Error);
    }

    [Fact]
    public async Task AGoogleTokenWithoutAnEmailIsRefused()
    {
        using var google = Google();

        var result = await google.VerifyAsync(Token(_keyA, claims: new Dictionary<string, object>()), null, Ct);

        Assert.Equal(IdentityErrors.ExternalEmailMissing, result.Error);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData("true", true)]
    [InlineData(false, false)]
    [InlineData("false", false)]
    public async Task AppleEmailVerifiedMayBeABooleanOrAString(object emailVerified, bool expected)
    {
        using var apple = Apple();

        var result = await apple.VerifyAsync(
            AppleToken(_keyA, new Dictionary<string, object> { ["email"] = "x7k2m9p4qd@privaterelay.appleid.com", ["email_verified"] = emailVerified }),
            null,
            Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExternalProviders.Apple, result.Value.Provider);
        Assert.Equal("001234.abcdef0123456789.1234", result.Value.Subject);
        Assert.Equal("x7k2m9p4qd@privaterelay.appleid.com", result.Value.Email);
        Assert.Equal(expected, result.Value.EmailVerified);
    }

    [Fact]
    public async Task AnAppleEmailWithoutTheFlagCountsAsVerified()
    {
        using var apple = Apple();

        var result = await apple.VerifyAsync(AppleToken(_keyA, new Dictionary<string, object> { ["email"] = "ada@icloud.com" }), null, Ct);

        Assert.True(result.Value.EmailVerified);
    }

    [Fact]
    public async Task AnAppleTokenWithoutAnEmailIsStillAnIdentity()
    {
        using var apple = Apple();

        var result = await apple.VerifyAsync(AppleToken(_keyA, new Dictionary<string, object>()), null, Ct);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Email);
        Assert.False(result.Value.EmailVerified);
    }

    [Fact]
    public async Task AGoogleTokenIsNotAnAppleToken()
    {
        using var apple = Apple();

        Assert.Equal(IdentityErrors.ExternalTokenInvalid, (await apple.VerifyAsync(Token(_keyA), null, Ct)).Error);
    }

    [Fact]
    public async Task TheNonceMustMatchWhenTheAppSendsOne()
    {
        using var apple = Apple();
        const string raw = "c0ffee-nonce-1234";
        var hashed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

        var withHash = AppleToken(_keyA, new Dictionary<string, object> { ["email"] = "ada@icloud.com", ["nonce"] = hashed });
        var withRaw = AppleToken(_keyA, new Dictionary<string, object> { ["email"] = "ada@icloud.com", ["nonce"] = raw });
        var without = AppleToken(_keyA, new Dictionary<string, object> { ["email"] = "ada@icloud.com" });

        Assert.True((await apple.VerifyAsync(withHash, raw, Ct)).IsSuccess);
        Assert.True((await apple.VerifyAsync(withRaw, raw, Ct)).IsSuccess);
        Assert.True((await apple.VerifyAsync(withHash, nonce: null, Ct)).IsSuccess);
        Assert.Equal(IdentityErrors.ExternalTokenInvalid, (await apple.VerifyAsync(withHash, "another-nonce", Ct)).Error);
        Assert.Equal(IdentityErrors.ExternalTokenInvalid, (await apple.VerifyAsync(without, raw, Ct)).Error);
    }

    [Fact]
    public async Task AProviderWithoutClientIdsIsDisabledAndFetchesNothing()
    {
        _options.Google.ClientIds = ["", "  "];
        _options.Apple.ClientIds = [];
        using var google = Google();
        using var apple = Apple();

        Assert.Equal(IdentityErrors.ExternalProviderDisabled, (await google.VerifyAsync(Token(_keyA), null, Ct)).Error);
        Assert.Equal(IdentityErrors.ExternalProviderDisabled, (await apple.VerifyAsync(Token(_keyA), null, Ct)).Error);
        Assert.Equal(0, _jwks.Requests);
    }

    [Fact]
    public async Task TheDispatcherPicksTheVerifierByProvider()
    {
        using var google = Google();
        using var apple = Apple();
        var verifier = new ExternalTokenVerifier(google, apple);
        var appleToken = AppleToken(_keyA, new Dictionary<string, object> { ["email"] = "ada@icloud.com" });

        Assert.Equal(ExternalProviders.Google, (await verifier.VerifyAsync("google", Token(_keyA), null, Ct)).Value.Provider);
        Assert.Equal(ExternalProviders.Apple, (await verifier.VerifyAsync("apple", appleToken, null, Ct)).Value.Provider);
        Assert.Equal(IdentityErrors.ExternalTokenInvalid, (await verifier.VerifyAsync("google", appleToken, null, Ct)).Error);
        Assert.Equal(IdentityErrors.ExternalTokenInvalid, (await verifier.VerifyAsync("facebook", Token(_keyA), null, Ct)).Error);
    }

    [Fact]
    public void ProvidersUseTheirPublishedKeySets()
    {
        Assert.Equal("https://www.googleapis.com/oauth2/v3/certs", GoogleTokenVerifier.JwksUri.ToString());
        Assert.Equal("https://appleid.apple.com/auth/keys", AppleTokenVerifier.JwksUri.ToString());
    }

    /// <summary>Serves whatever key set was last published and counts the requests.</summary>
    private sealed class FakeJwksHandler : HttpMessageHandler
    {
        private string _json = """{"keys":[]}""";
        private int _requests;

        public int Requests => _requests;

        public bool Fail { get; set; }

        public void Publish(params RsaSecurityKey[] keys)
        {
            var jwks = keys.Select(key =>
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
                return new Dictionary<string, string> { ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = key.KeyId, ["n"] = jwk.N, ["e"] = jwk.E };
            });

            _json = JsonSerializer.Serialize(new { keys = jwks });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            return Task.FromResult(Fail
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
