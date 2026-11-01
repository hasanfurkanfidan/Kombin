using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

public sealed class IdentityFlowTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegisterReturnsTokensAndCreatesTheAccount()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();

        using var response = await client.RegisterAsync(email.ToUpperInvariant(), displayName: "  Ada Lovelace  ", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tokens = await response.ReadTokensAsync(Ct);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Equal(900, tokens.ExpiresIn);

        using var me = await client.GetMeAsync(tokens.AccessToken, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal("Ada Lovelace", body.GetProperty("displayName").GetString());
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
        Assert.True(body.GetProperty("createdAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.False(body.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task RegisterNeverStoresThePasswordOrTheRawRefreshToken()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();

        var tokens = await client.RegisterAndReadAsync(email, Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        var user = await db.Users.SingleAsync(candidate => candidate.Email == email, Ct);
        Assert.DoesNotContain(ApiClientExtensions.DefaultPassword, user.PasswordHash, StringComparison.Ordinal);

        var stored = await db.RefreshTokens.SingleAsync(token => token.UserId == user.Id, Ct);
        Assert.NotEqual(tokens.RefreshToken, stored.TokenHash);
        Assert.Equal(JwtTokenService.HashRefreshToken(tokens.RefreshToken), stored.TokenHash);
    }

    [Fact]
    public async Task RegisteringTheSameEmailTwiceReturnsConflict()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();
        await client.RegisterAndReadAsync(email, Ct);

        using var duplicate = await client.RegisterAsync(email.ToUpperInvariant(), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await duplicate.ReadProblemAsync(Ct);
        Assert.Equal("Identity.EmailTaken", problem.GetProperty("errorCode").GetString());
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task RegisterValidationFailuresReturnAnErrorsDictionary()
    {
        using var client = factory.CreateClient();

        using var response = await client.RegisterAsync("not-an-email", "short", "A", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        var errors = problem.GetProperty("errors");
        Assert.NotEmpty(errors.GetProperty("email").EnumerateArray());
        Assert.NotEmpty(errors.GetProperty("password").EnumerateArray());
        Assert.NotEmpty(errors.GetProperty("displayName").EnumerateArray());
        Assert.Equal("Validation.Failed", problem.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task MalformedJsonReturnsBadRequestNotServerError()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("{ this is not json", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/v1/auth/register", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.ReadProblemAsync(Ct);
    }

    [Fact]
    public async Task LoginReturnsFreshTokensAndRecordsTheLoginTime()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();
        await client.RegisterAndReadAsync(email, Ct);

        using var response = await client.LoginAsync(email.ToUpperInvariant(), ApiClientExtensions.DefaultPassword, "Pixel 9", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.ReadTokensAsync(Ct);
        using var me = await client.GetMeAsync(tokens.AccessToken, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        var hash = JwtTokenService.HashRefreshToken(tokens.RefreshToken);
        var stored = await db.RefreshTokens.SingleAsync(token => token.TokenHash == hash, Ct);
        Assert.Equal("Pixel 9", stored.DeviceName);
        Assert.NotNull(await db.Users.Where(user => user.Email == email).Select(user => user.LastLoginAt).SingleAsync(Ct));
    }

    [Fact]
    public async Task WrongPasswordAndUnknownEmailReturnTheSameUnauthorizedBody()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();
        await client.RegisterAndReadAsync(email, Ct);

        using var wrongPassword = await client.LoginAsync(email, "Wrong-passw0rd", cancellationToken: Ct);
        using var unknownEmail = await client.LoginAsync(ApiClientExtensions.NewEmail(), ApiClientExtensions.DefaultPassword, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        var first = await wrongPassword.ReadProblemAsync(Ct);
        var second = await unknownEmail.ReadProblemAsync(Ct);
        Assert.Equal("Identity.InvalidCredentials", first.GetProperty("errorCode").GetString());
        Assert.Equal(WithoutRequestIdentifiers(first), WithoutRequestIdentifiers(second));
    }

    [Fact]
    public async Task RefreshRotatesTheTokenAndLinksTheOldOneToItsReplacement()
    {
        using var client = factory.CreateClient();
        var original = await client.RegisterAndReadAsync(cancellationToken: Ct);

        using var response = await client.RefreshAsync(original.RefreshToken, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = await response.ReadTokensAsync(Ct);
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        Assert.NotEqual(original.AccessToken, rotated.AccessToken);

        using var me = await client.GetMeAsync(rotated.AccessToken, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        var oldHash = JwtTokenService.HashRefreshToken(original.RefreshToken);
        var newHash = JwtTokenService.HashRefreshToken(rotated.RefreshToken);
        var oldToken = await db.RefreshTokens.SingleAsync(token => token.TokenHash == oldHash, Ct);
        var newToken = await db.RefreshTokens.SingleAsync(token => token.TokenHash == newHash, Ct);
        Assert.NotNull(oldToken.RevokedAt);
        Assert.Equal(newToken.Id, oldToken.ReplacedByTokenId);
        Assert.Null(newToken.RevokedAt);
        Assert.True(newToken.ExpiresAt > DateTimeOffset.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task ReusingARotatedTokenRevokesEveryActiveTokenOfTheUser()
    {
        using var client = factory.CreateClient();
        var original = await client.RegisterAndReadAsync(cancellationToken: Ct);
        using var rotation = await client.RefreshAsync(original.RefreshToken, Ct);
        var rotated = await rotation.ReadTokensAsync(Ct);

        using var replay = await client.RefreshAsync(original.RefreshToken, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var problem = await replay.ReadProblemAsync(Ct);
        Assert.Equal("Identity.InvalidRefreshToken", problem.GetProperty("errorCode").GetString());

        // The legitimate descendant is revoked too, because the chain is presumed compromised.
        using var descendant = await client.RefreshAsync(rotated.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, descendant.StatusCode);
    }

    [Fact]
    public async Task ReuseOnlyAffectsTheOffendingUsersTokens()
    {
        using var client = factory.CreateClient();
        var victim = await client.RegisterAndReadAsync(cancellationToken: Ct);
        var bystander = await client.RegisterAndReadAsync(cancellationToken: Ct);
        using var rotation = await client.RefreshAsync(victim.RefreshToken, Ct);
        await rotation.ReadTokensAsync(Ct);
        using var replay = await client.RefreshAsync(victim.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var other = await client.RefreshAsync(bystander.RefreshToken, Ct);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task ParallelRefreshesOfOneTokenYieldExactlyOneWinner()
    {
        using var client = factory.CreateClient();
        var original = await client.RegisterAndReadAsync(cancellationToken: Ct);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.RefreshAsync(original.RefreshToken, Ct)));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.All(
                responses.Where(response => response.StatusCode != HttpStatusCode.OK),
                response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
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
    public async Task ExpiredRefreshTokenIsRejected()
    {
        using var client = factory.CreateClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);
        var hash = JwtTokenService.HashRefreshToken(tokens.RefreshToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            await db.RefreshTokens
                .Where(token => token.TokenHash == hash)
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
        }

        using var response = await client.RefreshAsync(tokens.RefreshToken, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("definitely-not-a-real-refresh-token")]
    public async Task UnknownOrEmptyRefreshTokenIsRejected(string refreshToken)
    {
        using var client = factory.CreateClient();

        using var response = await client.RefreshAsync(refreshToken, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Identity.InvalidRefreshToken", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task LogoutRevokesTheRefreshToken()
    {
        using var client = factory.CreateClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);

        using var logout = await client.LogoutAsync(tokens.AccessToken, tokens.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var refresh = await client.RefreshAsync(tokens.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task LogoutCannotRevokeSomeoneElsesToken()
    {
        using var client = factory.CreateClient();
        var attacker = await client.RegisterAndReadAsync(cancellationToken: Ct);
        var victim = await client.RegisterAndReadAsync(cancellationToken: Ct);

        using var logout = await client.LogoutAsync(attacker.AccessToken, victim.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var refresh = await client.RefreshAsync(victim.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    [Fact]
    public async Task LogoutWithoutBearerTokenReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);

        using var logout = await client.LogoutAsync(accessToken: null, tokens.RefreshToken, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);
        using var refresh = await client.RefreshAsync(tokens.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    [Fact]
    public async Task MeWithoutTokenReturnsProblemJsonWithCorrelationId()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetMeAsync(accessToken: null, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        var headerValue = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
        Assert.Equal(headerValue, problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task MeWithGarbageTokenReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetMeAsync("not.a.jwt", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await response.ReadProblemAsync(Ct);
    }

    [Fact]
    public async Task WellFormedCorrelationIdIsEchoedAndMalformedOneIsReplaced()
    {
        using var client = factory.CreateClient();

        using var echoed = await client.GetMeAsync(accessToken: null, correlationId: "client-trace:42", cancellationToken: Ct);
        Assert.Equal("client-trace:42", Assert.Single(echoed.Headers.GetValues("X-Correlation-ID")));
        var problem = await echoed.ReadProblemAsync(Ct);
        Assert.Equal("client-trace:42", problem.GetProperty("correlationId").GetString());

        using var replaced = await client.GetMeAsync(accessToken: null, correlationId: "bad id with spaces", cancellationToken: Ct);
        var generated = Assert.Single(replaced.Headers.GetValues("X-Correlation-ID"));
        Assert.NotEqual("bad id with spaces", generated);
        Assert.Matches("^[A-Za-z0-9_.:-]{1,64}$", generated);
    }

    [Fact]
    public async Task LivenessAndReadinessEndpointsAreHealthy()
    {
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live", Ct);
        using var ready = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task OpenApiDocumentIsServedInDevelopment()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/auth/register", out _));
    }

    /// <summary>Flattens a problem document minus the fields that legitimately differ per request.</summary>
    private static string WithoutRequestIdentifiers(JsonElement problem) =>
        string.Join(
            '|',
            problem.EnumerateObject()
                .Where(property => property.Name is not ("correlationId" or "traceId"))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}={property.Value}"));
}
