using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary><c>POST /auth/external</c> end to end, with <see cref="FakeExternalTokenVerifier"/> standing in for Google and Apple.</summary>
public sealed class ExternalSignInTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TokenPair> SignInOkAsync(HttpClient client, string provider, string idToken, string? displayName = null)
    {
        using var response = await client.ExternalSignInAsync(provider, idToken, displayName, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadTokensAsync(Ct);
    }

    private static async Task<JsonElement> MeAsync(HttpClient client, string accessToken)
    {
        using var me = await client.GetMeAsync(accessToken, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        return await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<T> InDbAsync<T>(Func<KombinDbContext, Task<T>> query)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<KombinDbContext>());
    }

    [Fact]
    public async Task AFirstGoogleSignInCreatesAPasswordlessUserWhoseTokensWork()
    {
        using var client = factory.CreateClient();
        var subject = FakeExternalTokenVerifier.NewSubject();
        var email = ApiClientExtensions.NewEmail();

        var tokens = await SignInOkAsync(client, "google", FakeExternalTokenVerifier.Token(subject, email.ToUpperInvariant(), name: "Ada Lovelace"));

        Assert.True(tokens.IsNewUser);
        Assert.Equal(900, tokens.ExpiresIn);
        var me = await MeAsync(client, tokens.AccessToken);
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal("Ada Lovelace", me.GetProperty("displayName").GetString());

        var userId = me.GetProperty("id").GetGuid();
        var user = await InDbAsync(db => db.Users.SingleAsync(candidate => candidate.Id == userId, Ct));
        Assert.Null(user.PasswordHash);
        Assert.NotNull(user.LastLoginAt);
        var link = await InDbAsync(db => db.ExternalLogins.SingleAsync(login => login.UserId == userId, Ct));
        Assert.Equal(("google", subject, email), (link.Provider, link.Subject, link.EmailAtLink));

        // The refresh token is the same as a password login's: it rotates, and the new pair is not a new user.
        using var refreshed = await client.RefreshAsync(tokens.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.False((await refreshed.ReadTokensAsync(Ct)).IsNewUser);
    }

    [Fact]
    public async Task SigningInAgainWithTheSameSubjectReturnsTheSameUser()
    {
        using var client = factory.CreateClient();
        var subject = FakeExternalTokenVerifier.NewSubject();
        var email = ApiClientExtensions.NewEmail();

        var first = await SignInOkAsync(client, "apple", FakeExternalTokenVerifier.Token(subject, email), displayName: "Grace Hopper");
        // Later Apple tokens may lack the email, and the app no longer has the name: the subject alone finds the account.
        var second = await SignInOkAsync(client, "apple", FakeExternalTokenVerifier.Token(subject, email: null), displayName: "Someone Else");

        Assert.True(first.IsNewUser);
        Assert.False(second.IsNewUser);
        var firstMe = await MeAsync(client, first.AccessToken);
        var secondMe = await MeAsync(client, second.AccessToken);
        Assert.Equal(firstMe.GetProperty("id").GetGuid(), secondMe.GetProperty("id").GetGuid());
        Assert.Equal("Grace Hopper", secondMe.GetProperty("displayName").GetString());
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(user => user.Email == email, Ct)));
    }

    [Fact]
    public async Task ASocialOnlyUserCannotSignInWithAPassword()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();
        await SignInOkAsync(client, "google", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), email));

        using var login = await client.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        using var unknown = await client.LoginAsync(ApiClientExtensions.NewEmail(), ApiClientExtensions.DefaultPassword, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var problem = await login.ReadProblemAsync(Ct);
        var unknownProblem = await unknown.ReadProblemAsync(Ct);
        Assert.Equal("Identity.InvalidCredentials", problem.GetProperty("errorCode").GetString());
        Assert.Equal(unknownProblem.GetProperty("detail").GetString(), problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task APasswordUserLinksGoogleByEmailAndHasTwoWaysIn()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();
        var registered = await client.RegisterAndReadAsync(email, Ct);
        Assert.True(registered.IsNewUser);
        var userId = (await MeAsync(client, registered.AccessToken)).GetProperty("id").GetGuid();
        var subject = FakeExternalTokenVerifier.NewSubject();

        var google = await SignInOkAsync(client, "google", FakeExternalTokenVerifier.Token(subject, email.ToUpperInvariant(), name: "Not Applied"));

        Assert.False(google.IsNewUser);
        var me = await MeAsync(client, google.AccessToken);
        Assert.Equal(userId, me.GetProperty("id").GetGuid());
        Assert.Equal("Test User", me.GetProperty("displayName").GetString());

        using var login = await client.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False((await login.ReadTokensAsync(Ct)).IsNewUser);

        var again = await SignInOkAsync(client, "google", FakeExternalTokenVerifier.Token(subject, email));
        Assert.Equal(userId, (await MeAsync(client, again.AccessToken)).GetProperty("id").GetGuid());
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(user => user.Email == email, Ct)));
        Assert.Equal(1, await InDbAsync(db => db.ExternalLogins.CountAsync(link => link.UserId == userId, Ct)));
    }

    [Fact]
    public async Task GoogleAndAppleWithTheSameEmailAreOneUser()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();

        var google = await SignInOkAsync(client, "google", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), email));
        var apple = await SignInOkAsync(client, "apple", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), email));

        Assert.True(google.IsNewUser);
        Assert.False(apple.IsNewUser);
        var userId = (await MeAsync(client, google.AccessToken)).GetProperty("id").GetGuid();
        Assert.Equal(userId, (await MeAsync(client, apple.AccessToken)).GetProperty("id").GetGuid());
        Assert.Equal(2, await InDbAsync(db => db.ExternalLogins.CountAsync(link => link.UserId == userId, Ct)));
    }

    [Fact]
    public async Task TheDisplayNameFallsBackFromTheRequestToTheTokenToTheEmail()
    {
        using var client = factory.CreateClient();
        var relay = $"r{Guid.NewGuid():N}"[..12];

        var fromRequest = await SignInOkAsync(
            client, "apple", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), ApiClientExtensions.NewEmail(), name: "Token Name"), "  Request Name  ");
        var fromToken = await SignInOkAsync(
            client, "google", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), ApiClientExtensions.NewEmail(), name: "Token Name"), "   ");
        var fromEmail = await SignInOkAsync(
            client, "apple", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), $"{relay}@privaterelay.appleid.com"));

        Assert.Equal("Request Name", (await MeAsync(client, fromRequest.AccessToken)).GetProperty("displayName").GetString());
        Assert.Equal("Token Name", (await MeAsync(client, fromToken.AccessToken)).GetProperty("displayName").GetString());
        var relayMe = await MeAsync(client, fromEmail.AccessToken);
        Assert.Equal(relay, relayMe.GetProperty("displayName").GetString());
        Assert.Equal($"{relay}@privaterelay.appleid.com", relayMe.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnlinkedTokenWithoutAVerifiedEmailIsRefused(bool emailPresent)
    {
        using var client = factory.CreateClient();
        var email = emailPresent ? ApiClientExtensions.NewEmail() : null;

        using var response = await client.ExternalSignInAsync("apple", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), email, verified: false), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Identity.ExternalEmailMissing", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        if (email is not null)
        {
            Assert.False(await InDbAsync(db => db.Users.AnyAsync(user => user.Email == email, Ct)));
        }
    }

    [Theory]
    [InlineData("invalid", HttpStatusCode.Unauthorized, "Identity.ExternalTokenInvalid")]
    [InlineData("disabled", HttpStatusCode.ServiceUnavailable, "Identity.ExternalProviderDisabled")]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable, "Identity.ExternalProviderUnavailable")]
    public async Task VerifierFailuresBecomeProblems(string idToken, HttpStatusCode status, string errorCode)
    {
        using var client = factory.CreateClient();

        using var response = await client.ExternalSignInAsync("google", idToken, cancellationToken: Ct);

        Assert.Equal(status, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task TheRequestIsValidated()
    {
        using var client = factory.CreateClient();

        using var response = await client.ExternalSignInAsync("facebook", "", new string('x', 51), new string('d', 101), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ReadProblemAsync(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("provider", out _));
        Assert.True(errors.TryGetProperty("idToken", out _));
        Assert.True(errors.TryGetProperty("displayName", out _));
        Assert.True(errors.TryGetProperty("deviceName", out _));
    }

    [Fact]
    public async Task TheProviderNameIsCaseInsensitive()
    {
        using var client = factory.CreateClient();
        var subject = FakeExternalTokenVerifier.NewSubject();

        var tokens = await SignInOkAsync(client, "Google", FakeExternalTokenVerifier.Token(subject, ApiClientExtensions.NewEmail()));

        var userId = (await MeAsync(client, tokens.AccessToken)).GetProperty("id").GetGuid();
        Assert.Equal("google", (await InDbAsync(db => db.ExternalLogins.SingleAsync(link => link.UserId == userId, Ct))).Provider);
    }

    [Fact]
    public async Task SimultaneousFirstSignInsCreateOneUser()
    {
        using var client = factory.CreateClient();
        var subject = FakeExternalTokenVerifier.NewSubject();
        var email = ApiClientExtensions.NewEmail();
        var token = FakeExternalTokenVerifier.Token(subject, email);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.ExternalSignInAsync("google", token, cancellationToken: Ct)));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var pairs = await Task.WhenAll(responses.Select(response => response.ReadTokensAsync(Ct)));
            Assert.Single(pairs, pair => pair.IsNewUser);
            Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(user => user.Email == email, Ct)));
            Assert.Equal(1, await InDbAsync(db => db.ExternalLogins.CountAsync(link => link.Subject == subject, Ct)));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task TheDeviceNameIsKeptOnTheRefreshToken()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();

        using var response = await client.ExternalSignInAsync(
            "google", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), email), deviceName: "Pixel 9", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deviceName = await InDbAsync(db => db.RefreshTokens
            .Where(token => db.Users.Any(user => user.Id == token.UserId && user.Email == email))
            .Select(token => token.DeviceName)
            .SingleAsync(Ct));
        Assert.Equal("Pixel 9", deviceName);
    }
}
