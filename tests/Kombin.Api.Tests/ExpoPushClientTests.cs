using System.Net;
using System.Text;
using System.Text.Json;
using Kombin.Api.Features.Notification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Tests;

public class ExpoPushClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));

            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ExpoPushClient Client(StubHandler handler, string? accessToken = null) =>
        new(new HttpClient(handler), Options.Create(new ExpoOptions { AccessToken = accessToken }), NullLogger<ExpoPushClient>.Instance);

    private static ExpoPushMessage Message(string to) =>
        new(to, "Başlık", "Gövde", JsonDocument.Parse("""{"deepLink":"kombin://notifications"}""").RootElement.Clone());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendingPostsTheMessagesAsCamelCaseJsonToTheExpoEndpoint()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"data":[{"status":"ok","id":"t-1"}]}"""));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]")], Ct);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(ExpoPushClient.SendUrl, request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);

        using var document = JsonDocument.Parse(body);
        var sent = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("ExponentPushToken[a]", sent.GetProperty("to").GetString());
        Assert.Equal("Başlık", sent.GetProperty("title").GetString());
        Assert.Equal("Gövde", sent.GetProperty("body").GetString());
        Assert.Equal("kombin://notifications", sent.GetProperty("data").GetProperty("deepLink").GetString());
        Assert.Equal("default", sent.GetProperty("sound").GetString());
        Assert.Equal("high", sent.GetProperty("priority").GetString());

        Assert.True(result.Succeeded);
        var ticket = Assert.Single(result.Tickets);
        Assert.True(ticket.IsOk);
        Assert.Equal("t-1", ticket.Id);
    }

    [Fact]
    public async Task ErrorTicketsCarryTheirErrorCodeAndMessage()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"data":[{"status":"error","message":"not registered","details":{"error":"DeviceNotRegistered"}},{"status":"ok","id":"t-2"}]}
            """));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]"), Message("ExponentPushToken[b]")], Ct);

        Assert.True(result.Succeeded);
        Assert.False(result.Tickets[0].IsOk);
        Assert.Equal("DeviceNotRegistered", result.Tickets[0].Error);
        Assert.Equal("not registered", result.Tickets[0].Message);
        Assert.True(result.Tickets[1].IsOk);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ANon2xxAnswerIsAFailedResultNotAnException(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => Json(status, """{"errors":[{"code":"BOOM","message":"nope"}]}"""));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]")], Ct);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Tickets);
        Assert.StartsWith($"HTTP {(int)status}", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANetworkErrorIsAFailedResultNotAnException()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));

        var send = await Client(handler).SendAsync([Message("ExponentPushToken[a]")], Ct);
        var receipts = await Client(handler).GetReceiptsAsync(["t-1"], Ct);

        Assert.Equal("connection refused", send.Error);
        Assert.Equal("connection refused", receipts.Error);
    }

    [Fact]
    public async Task ATimeoutIsAFailedResultButACallerCancellationStillCancels()
    {
        var timeout = new StubHandler((_, _) => throw new TaskCanceledException("timed out"));
        Assert.False((await Client(timeout).SendAsync([Message("ExponentPushToken[a]")], Ct)).Succeeded);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new StubHandler((_, _) => throw new TaskCanceledException("cancelled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).SendAsync([Message("ExponentPushToken[a]")], cancelled.Token));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"data":{"status":"ok"}}""")]
    [InlineData("""{"unexpected":true}""")]
    public async Task AnUnreadableBodyIsAFailedResult(string body)
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, body));

        Assert.False((await Client(handler).SendAsync([Message("ExponentPushToken[a]")], Ct)).Succeeded);
    }

    [Fact]
    public async Task ADifferentNumberOfTicketsThanMessagesIsAFailedResult()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"data":[{"status":"ok","id":"t-1"}]}"""));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]"), Message("ExponentPushToken[b]")], Ct);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Tickets);
    }

    [Fact]
    public async Task TheAccessTokenIsSentAsABearerOnlyWhenConfigured()
    {
        var plain = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"data":[{"status":"ok","id":"t"}]}"""));
        var secured = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"data":[{"status":"ok","id":"t"}]}"""));

        await Client(plain).SendAsync([Message("ExponentPushToken[a]")], Ct);
        await Client(secured, "secret-token").SendAsync([Message("ExponentPushToken[a]")], Ct);

        Assert.Null(plain.Calls.Single().Request.Headers.Authorization);
        var authorization = secured.Calls.Single().Request.Headers.Authorization;
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("secret-token", authorization?.Parameter);
    }

    [Fact]
    public async Task ReceiptsArePostedByTicketIdAndParsedByKey()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"data":{"t-1":{"status":"ok"},"t-2":{"status":"error","message":"gone","details":{"error":"DeviceNotRegistered"}}}}
            """));

        var result = await Client(handler).GetReceiptsAsync(["t-1", "t-2", "t-3"], Ct);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(ExpoPushClient.ReceiptsUrl, request.RequestUri!.ToString());
        using (var document = JsonDocument.Parse(body))
        {
            Assert.Equal(["t-1", "t-2", "t-3"], document.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()));
        }

        Assert.True(result.Succeeded);
        Assert.True(result.Receipts["t-1"].IsOk);
        Assert.Equal("DeviceNotRegistered", result.Receipts["t-2"].Error);
        Assert.False(result.Receipts.ContainsKey("t-3"));
    }

    [Fact]
    public async Task AFailedReceiptsCallIsAFailedResult()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.BadGateway, "{}"));

        var result = await Client(handler).GetReceiptsAsync(["t-1"], Ct);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Receipts);
    }
}
