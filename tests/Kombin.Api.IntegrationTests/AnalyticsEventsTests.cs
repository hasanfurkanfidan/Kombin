using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Analytics;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.AnalyticsClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class AnalyticsEventsTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<(int Index, string Reason)> Rejections(JsonElement body) =>
        body.GetProperty("rejected").EnumerateArray().Select(entry => (entry.GetProperty("index").GetInt32(), entry.GetProperty("reason").GetString()!));

    [Fact]
    public async Task AnAnonymousBatchIsAcceptedAndStoredWithItsAnonymousId()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-2);

        var body = await client.PostEventsOkAsync(
            [
                Event("app_open", occurredAt: occurredAt, anonymousId: anonymousId),
                Event("screen_view", new { screen = "product_detail" }, platform: "Android", anonymousId: anonymousId),
            ],
            Ct);

        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Empty(body.GetProperty("rejected").EnumerateArray());

        var stored = await factory.EventsOfAnonymousIdAsync(anonymousId, Ct);
        Assert.Equal(["app_open", "screen_view"], stored.Select(analyticsEvent => analyticsEvent.Name));
        Assert.All(stored, analyticsEvent => Assert.Null(analyticsEvent.UserId));
        Assert.Equal(AnalyticsPlatform.Ios, stored[0].Platform);
        Assert.Equal(AnalyticsPlatform.Android, stored[1].Platform);
        Assert.Equal("1.0.0", stored[0].AppVersion);
        Assert.InRange(Math.Abs((stored[0].OccurredAt - occurredAt).TotalMilliseconds), 0, 1);
        Assert.True(stored[0].ReceivedAt > stored[0].OccurredAt);
    }

    [Fact]
    public async Task ASignedInBatchIsAttributedToTheCallerWhetherOrNotItCarriesAnAnonymousId()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var anonymousId = NewAnonymousId();
        var session = $"session-{Guid.NewGuid():N}";

        await user.Client.PostEventsOkAsync(
            [
                Event("app_open", sessionId: session, anonymousId: anonymousId),
                Event("app_background", sessionId: session),
            ],
            Ct);

        var stored = await factory.WithDbAsync(db => db.AnalyticsEvents.Where(analyticsEvent => analyticsEvent.SessionId == session).OrderBy(analyticsEvent => analyticsEvent.Id).ToListAsync(Ct));
        Assert.Equal(2, stored.Count);
        Assert.All(stored, analyticsEvent => Assert.Equal(user.Id, analyticsEvent.UserId));
        Assert.Equal(anonymousId, stored[0].AnonymousId);
        Assert.Null(stored[1].AnonymousId);
    }

    [Fact]
    public async Task AnAnonymousEventWithoutAnAnonymousIdIsRejectedAndTheRestIsStored()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();

        var body = await client.PostEventsOkAsync([Event("app_open"), Event("app_open", anonymousId: anonymousId)], Ct);

        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal([(0, "missing_identity")], Rejections(body));
        Assert.Single(await factory.EventsOfAnonymousIdAsync(anonymousId, Ct));
    }

    [Fact]
    public async Task AnUnknownEventFailsTheWholeBatchWithItsOwnErrorCode()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();

        using var response = await client.PostEventsAsync([Event("app_open", anonymousId: anonymousId), Event("purchase_completed", anonymousId: anonymousId)], Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Analytics.UnknownEvent", problem.GetProperty("errorCode").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("events[1].name", out _));
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
        Assert.Empty(await factory.EventsOfAnonymousIdAsync(anonymousId, Ct));
    }

    [Fact]
    public async Task APropertyNamedLikePersonalDataFailsTheBatchAndNothingIsStored()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();

        using var response = await client.PostEventsAsync(
            [Event("screen_view", new Dictionary<string, object> { ["screen"] = "home", ["email"] = "ada@example.com" }, anonymousId: anonymousId)], Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Validation.Failed", problem.GetProperty("errorCode").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("events[0].properties.email", out _));
        Assert.Empty(await factory.EventsOfAnonymousIdAsync(anonymousId, Ct));
    }

    [Fact]
    public async Task ABatchOfZeroOrMoreThanFiftyEventsIsRefused()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();

        using var empty = await client.PostEventsAsync([], Ct);
        using var tooMany = await client.PostEventsAsync([.. Enumerable.Range(0, 51).Select(_ => Event(anonymousId: anonymousId))], Ct);
        using var fifty = await client.PostEventsAsync([.. Enumerable.Range(0, 50).Select(_ => Event(anonymousId: anonymousId))], Ct);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, fifty.StatusCode);
        Assert.Equal(50, (await factory.EventsOfAnonymousIdAsync(anonymousId, Ct)).Count);
    }

    [Fact]
    public async Task AClockThatIsOffIsClampedToTheServerTimeAndTheEventIsKept()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();
        var now = DateTimeOffset.UtcNow;

        var sessions = new Dictionary<string, DateTimeOffset>
        {
            ["tooOld"] = now.AddDays(-8),
            ["tooNew"] = now.AddMinutes(10),
            ["oldButFine"] = now.AddDays(-6),
            ["slightlyAhead"] = now.AddMinutes(4),
        };

        var body = await client.PostEventsOkAsync(
            [.. sessions.Select(pair => Event(sessionId: $"{pair.Key}-{anonymousId}", occurredAt: pair.Value, anonymousId: anonymousId))], Ct);

        Assert.Equal(4, body.GetProperty("accepted").GetInt32());
        Assert.Empty(body.GetProperty("rejected").EnumerateArray());

        var stored = (await factory.EventsOfAnonymousIdAsync(anonymousId, Ct)).ToDictionary(analyticsEvent => analyticsEvent.SessionId);

        foreach (var kept in new[] { "oldButFine", "slightlyAhead" })
        {
            Assert.InRange(Math.Abs((stored[$"{kept}-{anonymousId}"].OccurredAt - sessions[kept]).TotalMilliseconds), 0, 1);
        }

        foreach (var clamped in new[] { "tooOld", "tooNew" })
        {
            var analyticsEvent = stored[$"{clamped}-{anonymousId}"];
            Assert.Equal(analyticsEvent.ReceivedAt, analyticsEvent.OccurredAt);
        }
    }

    [Fact]
    public async Task RepeatsOfTheSameSessionNameAndTimeWithinABatchAreDroppedAndReported()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var session = $"session-{Guid.NewGuid():N}";
        var otherSession = $"session-{Guid.NewGuid():N}";

        var body = await client.PostEventsOkAsync(
            [
                Event("screen_view", new { screen = "home" }, session, occurredAt: at, anonymousId: anonymousId),
                Event("screen_view", new { screen = "search" }, session, occurredAt: at, anonymousId: anonymousId),
                Event("app_open", sessionId: session, occurredAt: at, anonymousId: anonymousId),
                Event("screen_view", new { screen = "home" }, otherSession, occurredAt: at, anonymousId: anonymousId),
                Event("screen_view", new { screen = "home" }, session, occurredAt: at.AddSeconds(1), anonymousId: anonymousId),
            ],
            Ct);

        Assert.Equal(4, body.GetProperty("accepted").GetInt32());
        Assert.Equal([(1, "duplicate")], Rejections(body));

        // The first one wins.
        var stored = await factory.EventsOfAnonymousIdAsync(anonymousId, Ct);
        Assert.Equal(4, stored.Count);
        Assert.Contains("\"home\"", stored.First(analyticsEvent => analyticsEvent.SessionId == session && analyticsEvent.Name == "screen_view").Properties, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PropertiesAreStoredAsAFlatJsonObjectWithoutNulls()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();
        var productId = Guid.NewGuid();

        await client.PostEventsOkAsync(
            [
                Event("product_view", new { productId, personaId = (Guid?)null, source = "feed" }, anonymousId: anonymousId),
                Event("search", new { queryLength = 12 }, anonymousId: anonymousId),
                Event("app_background", anonymousId: anonymousId),
            ],
            Ct);

        var stored = await factory.EventsOfAnonymousIdAsync(anonymousId, Ct);

        using var view = JsonDocument.Parse(stored[0].Properties);
        Assert.Equal(productId, view.RootElement.GetProperty("productId").GetGuid());
        Assert.Equal("feed", view.RootElement.GetProperty("source").GetString());
        Assert.False(view.RootElement.TryGetProperty("personaId", out _));
        Assert.Equal(12, JsonDocument.Parse(stored[1].Properties).RootElement.GetProperty("queryLength").GetInt32());
        Assert.Equal("{}", stored[2].Properties);
    }

    [Fact]
    public async Task LoginAttachesTheDevicesAnonymousEventsToTheUserAndOnlyThose()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();
        await client.RegisterAndReadAsync(email, Ct);
        var userId = await IdOfAsync(client, email);

        var anonymousId = NewAnonymousId();
        var otherDevice = NewAnonymousId();
        await client.PostEventsOkAsync([Event("app_open", anonymousId: anonymousId), Event("app_background", anonymousId: anonymousId), Event("app_open", anonymousId: otherDevice)], Ct);

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = ApiClientExtensions.DefaultPassword, anonymousId }, Ct);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.All(await factory.EventsOfAnonymousIdAsync(anonymousId, Ct), analyticsEvent => Assert.Equal(userId, analyticsEvent.UserId));
        Assert.All(await factory.EventsOfAnonymousIdAsync(otherDevice, Ct), analyticsEvent => Assert.Null(analyticsEvent.UserId));
    }

    [Fact]
    public async Task RegistrationAttachesTheDevicesAnonymousEventsToTheNewUser()
    {
        using var client = factory.CreateClient();
        var anonymousId = NewAnonymousId();
        await client.PostEventsOkAsync([Event("app_open", anonymousId: anonymousId)], Ct);
        var email = ApiClientExtensions.NewEmail();

        using var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register", new { email, password = ApiClientExtensions.DefaultPassword, displayName = "Test User", anonymousId }, Ct);

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var stored = Assert.Single(await factory.EventsOfAnonymousIdAsync(anonymousId, Ct));
        Assert.Equal(await IdOfAsync(client, email), stored.UserId);
    }

    [Fact]
    public async Task SigningInNeverTakesOverEventsThatAlreadyBelongToAnotherUser()
    {
        // A shared device: the first user's events carry the device's anonymous ID too.
        using var first = await factory.CreateUserAsync(Ct);
        var anonymousId = NewAnonymousId();
        await first.Client.PostEventsOkAsync([Event("app_open", anonymousId: anonymousId)], Ct);
        using var anonymous = factory.CreateClient();
        await anonymous.PostEventsOkAsync([Event("app_background", anonymousId: anonymousId)], Ct);

        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();
        using var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register", new { email, password = ApiClientExtensions.DefaultPassword, displayName = "Second User", anonymousId }, Ct);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var stored = await factory.EventsOfAnonymousIdAsync(anonymousId, Ct);
        Assert.Equal(first.Id, stored.Single(analyticsEvent => analyticsEvent.Name == "app_open").UserId);
        Assert.Equal(await IdOfAsync(client, email), stored.Single(analyticsEvent => analyticsEvent.Name == "app_background").UserId);
    }

    [Fact]
    public async Task ASignInWithoutAnAnonymousIdWorksAsBefore()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();
        await client.RegisterAndReadAsync(email, Ct);

        using var login = await client.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task AMalformedAnonymousIdOnLoginOrRegistrationIsAValidationError()
    {
        using var client = factory.CreateClient();
        var email = ApiClientExtensions.NewEmail();

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = ApiClientExtensions.DefaultPassword, anonymousId = "not valid!" }, Ct);
        using var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register", new { email, password = ApiClientExtensions.DefaultPassword, displayName = "Test User", anonymousId = new string('a', 65) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.True((await login.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("anonymousId", out _));
    }

    private static async Task<Guid> IdOfAsync(HttpClient client, string email)
    {
        using var login = await client.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        var tokens = await login.ReadTokensAsync(Ct);
        using var me = await client.GetMeAsync(tokens.AccessToken, cancellationToken: Ct);

        return (await me.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
