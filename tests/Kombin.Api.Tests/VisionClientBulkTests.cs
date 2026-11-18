using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kombin.Api.Common.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kombin.Api.Tests;

/// <summary><c>POST /v1/garment/bulk</c> through the real <see cref="VisionClient"/> (task BA2): the contract's JSON, <c>Retry-After</c>, 503 against 4xx, the per-call timeout.</summary>
public sealed class VisionClientBulkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Piece = """
        {
          "box": {"x": 0.07, "y": 0.48, "w": 0.46, "h": 0.40},
          "preTicked": false,
          "reasons": ["cut_by_frame", "a_new_reason"],
          "analysis": {
            "cutout": "Y3V0",
            "thumbnail": "dGh1bWI=",
            "category": {"slug": "ust-giyim/tisort", "confidence": 0.91},
            "categoryCandidates": [{"slug": "ust-giyim/tisort", "confidence": 0.91}, {"slug": "ust-giyim/gomlek", "confidence": 0.05}],
            "primaryColor": {"name": "Beige", "confidence": 0.8, "hex": "#D8C3A5"},
            "secondaryColor": null,
            "pattern": {"name": "Solid", "confidence": 0.95},
            "embedding": [1.0, 0.0, 0.0],
            "quality": {"isGarment": true, "confidence": 0.9, "warnings": ["blurry"]}
          }
        }
        """;

    [Fact]
    public async Task TheAnswerIsReadAsTheBriefDescribesIt()
    {
        var body = $$"""
            {"refusal": null, "pieces": [{{Piece}}],
             "unseparated": {"count": 2, "regions": [{"x": 0.1, "y": 0.2, "w": 0.4, "h": 0.3}]},
             "stagesMs": {"detector": 600, "total": 5300.5 }
            }
            """;
        var handler = new Handler(_ => Respond(HttpStatusCode.OK, body));

        var call = await Client(handler).BulkAsync([1, 2, 3], "image/jpeg", TimeSpan.FromSeconds(5), Ct);

        var bulk = Assert.IsType<VisionBulk>(call.Value);
        Assert.Null(bulk.Refusal);
        var piece = Assert.Single(bulk.Pieces);
        Assert.Equal(new VisionRegionBox(0.07, 0.48, 0.46, 0.40), piece.Box);
        Assert.False(piece.PreTicked);
        Assert.Equal(["cut_by_frame", "a_new_reason"], piece.Reasons);
        Assert.Equal("ust-giyim/tisort", piece.Analysis.Category!.Slug);
        Assert.Equal("Beige", piece.Analysis.PrimaryColor.Name);
        Assert.Null(piece.Analysis.SecondaryColor);
        Assert.Equal(["blurry"], piece.Analysis.Quality.Warnings);
        Assert.Equal(2, bulk.Unseparated!.Count);
        Assert.Single(bulk.Unseparated.Regions);
        Assert.Equal(5300.5, bulk.StagesMs!["total"]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/garment/bulk", request.Uri.AbsolutePath);
        Assert.Contains("name=image", request.Body, StringComparison.Ordinal);
        Assert.Contains("image/jpeg", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalIsAnAnswerWithNoPieces()
    {
        var handler = new Handler(_ => Respond(HttpStatusCode.OK, """{"refusal": "closet", "pieces": [], "unseparated": null, "stagesMs": {"total": 900}}"""));

        var call = await Client(handler).BulkAsync([1], "image/png", TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("closet", call.Value!.Refusal);
        Assert.Empty(call.Value.Pieces);
        Assert.Null(call.Value.Unseparated);
    }

    [Fact]
    public async Task ABusyServiceIsTransientAndSaysWhenToComeBack()
    {
        var handler = new Handler(_ => Respond(HttpStatusCode.ServiceUnavailable, """{"detail": "busy"}""", response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5))));

        var call = await Client(handler).BulkAsync([1], "image/jpeg", TimeSpan.FromSeconds(5), Ct);

        Assert.Null(call.Value);
        Assert.True(call.IsTransient);
        Assert.Equal(503, call.Status);
        Assert.Equal("busy", call.Detail);
        Assert.Equal(TimeSpan.FromSeconds(5), call.RetryAfter);
    }

    [Fact]
    public async Task ARetryAfterDateIsTheTimeFromNowAndAPastOneIsZero()
    {
        var later = new Handler(_ => Respond(HttpStatusCode.ServiceUnavailable, """{"detail": "busy"}""", response => response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30))));
        var past = new Handler(_ => Respond(HttpStatusCode.ServiceUnavailable, """{"detail": "busy"}""", response => response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-30))));

        var inThirty = (await Client(later).BulkAsync([1], "image/jpeg", TimeSpan.FromSeconds(5), Ct)).RetryAfter;
        var gone = (await Client(past).BulkAsync([1], "image/jpeg", TimeSpan.FromSeconds(5), Ct)).RetryAfter;

        Assert.InRange(inThirty!.Value, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(31));
        Assert.Equal(TimeSpan.Zero, gone);
    }

    [Fact]
    public async Task NoRetryAfterHeaderIsNull()
    {
        var call = await Client(new Handler(_ => Respond(HttpStatusCode.ServiceUnavailable, """{"detail": "busy"}"""))).BulkAsync([1], "image/jpeg", TimeSpan.FromSeconds(5), Ct);

        Assert.True(call.IsTransient);
        Assert.Null(call.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task OnlyTheServicesOwnRefusalOfTheFileIsPermanent(HttpStatusCode status, bool transient)
    {
        var call = await Client(new Handler(_ => Respond(status, """{"detail": "unsupported_image"}"""))).BulkAsync([1], "image/jpeg", TimeSpan.FromSeconds(5), Ct);

        Assert.Null(call.Value);
        Assert.Equal(transient, call.IsTransient);
        Assert.Equal((int)status, call.Status);
        Assert.Equal("unsupported_image", call.Detail);
        Assert.Null(call.RetryAfter);
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIsTransientAndNeverThrows()
    {
        var call = await Client(new Handler(_ => Respond(HttpStatusCode.OK, "<html>proxy error</html>"))).BulkAsync([1], "image/jpeg", TimeSpan.FromSeconds(5), Ct);

        Assert.True(call.IsTransient);
        Assert.StartsWith("vision_unreachable", call.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowServiceIsCutOffAtTheCallersTimeoutNotTheClients()
    {
        var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return Respond(HttpStatusCode.OK, "{}");
        });

        var started = DateTime.UtcNow;
        var call = await Client(handler).BulkAsync([1], "image/jpeg", TimeSpan.FromMilliseconds(150), Ct);

        Assert.True(call.IsTransient);
        Assert.StartsWith("vision_unreachable", call.Error, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ACallerThatCancelsGetsTheCancellationNotAValue()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler(async (_, token) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return Respond(HttpStatusCode.OK, "{}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).BulkAsync([1], "image/jpeg", TimeSpan.FromSeconds(30), cancel.Token));
    }

    [Fact]
    public async Task WithoutABaseAddressTheCallIsTransient()
    {
        using var http = new HttpClient(new Handler(_ => Respond(HttpStatusCode.OK, "{}")));

        var call = await new VisionClient(http, NullLogger<VisionClient>.Instance).BulkAsync([1], "image/jpeg", TimeSpan.FromSeconds(5), Ct);

        Assert.True(call.IsTransient);
    }

    private static VisionClient Client(Handler handler) =>
        new(new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://vision.test/"), Timeout = Timeout.InfiniteTimeSpan }, NullLogger<VisionClient>.Instance);

    private static HttpResponseMessage Respond(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(response);

        return response;
    }

    private sealed record Seen(HttpMethod Method, Uri Uri, string Body);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request)))
        {
        }

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.Method, request.RequestUri!, request.Content is null ? string.Empty : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken))));

            return await answer(request, cancellationToken);
        }
    }
}
