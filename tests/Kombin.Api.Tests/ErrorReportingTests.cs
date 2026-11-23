using System.Text;
using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Diagnostics;
using Microsoft.Extensions.Configuration;
using Sentry.Protocol;

namespace Kombin.Api.Tests;

/// <summary>
/// What leaves the server for Sentry (docs/privacy-notes.md, "Error reporting"). The scrubber is fed an event carrying every kind of personal data the
/// SDK or a careless caller could attach, and the JSON that would be sent is searched for each of them.
/// </summary>
public sealed class ErrorReportingTests
{
    private const string Email = "ayse.yilmaz@example.com";
    private const string Name = "Ayşe Yılmaz";
    private const string Ip = "203.0.113.42";
    private const string UserId = "0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.secret-bearer";
    private const string Cookie = "kombin_admin=cookie-secret";
    private const string Body = "{\"password\":\"hunter2\"}";
    private const string Query = "?email=ayse.yilmaz%40example.com";
    private const string PresignedUrl = "https://storage.kombin.app/wardrobe/0193a1b2/photo.jpg?X-Amz-Signature=abc";
    private const string StorageKey = "wardrobe/0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b/originals/photo.jpg";
    private const string MessageText = "duplicate key value (email)=(ayse.yilmaz@example.com)";

    private static readonly string[] Forbidden =
        [Email, "ayse.yilmaz", Name, Ip, UserId, Token, Cookie, "hunter2", Query, PresignedUrl, "X-Amz-Signature", StorageKey, "photo.jpg", MessageText, "the-machine"];

    [Fact]
    public void NothingPersonalSurvivesTheScrubber()
    {
        var json = Serialize(SentryScrubber.Scrub(EventFullOfPersonalData()));

        foreach (var value in Forbidden)
        {
            Assert.False(json.Contains(value, StringComparison.OrdinalIgnoreCase), $"'{value}' would leave the server: {json}");
        }
    }

    [Fact]
    public void TheScrubberKeepsWhatIsNeededToFindTheBug()
    {
        var clean = SentryScrubber.Scrub(EventFullOfPersonalData());
        var json = Serialize(clean);

        Assert.Equal(SentryLevel.Error, clean.Level);
        Assert.Equal("kombin-api@abc1234", clean.Release);
        Assert.Equal("Production", clean.Environment);
        Assert.Equal("/api/v1/wardrobe/items/{id}", clean.TransactionName);
        Assert.Equal(
            ["correlation_id", "error_code", "kind", "source"],
            clean.Tags.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("request", clean.Tags["kind"]);
        Assert.Equal("/api/v1/wardrobe/items/{id}", clean.Tags["source"]);
        Assert.Equal("Npgsql.PostgresException", clean.Tags["error_code"]);
        Assert.Equal("0HN7ABC:00000001", clean.Tags["correlation_id"]);

        var exception = Assert.Single(clean.SentryExceptions!);
        Assert.Equal("Npgsql.PostgresException", exception.Type);
        Assert.Null(exception.Value);
        Assert.Empty(exception.Mechanism!.Data);
        var frame = Assert.Single(exception.Stacktrace!.Frames);
        Assert.Equal("Kombin.Api.Features.Wardrobe.GetItem.Endpoint.Handle", frame.Function);
        Assert.Equal(42, frame.LineNumber);
        Assert.Equal("Endpoint.cs", frame.FileName);
        Assert.Null(frame.AbsolutePath);
        Assert.Null(clean.DebugImages);
        Assert.Empty(frame.Vars);
        Assert.Null(frame.ContextLine);

        Assert.Contains("runtime", clean.Contexts.Keys);
        Assert.DoesNotContain("device", clean.Contexts.Keys);
        Assert.DoesNotContain("\"user\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"request\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("breadcrumbs", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"extra\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("server_name", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ATagThatLooksLikeFreeTextIsDropped()
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.SetTag("source", "/api/v1/search?q=" + Email);
        sentryEvent.SetTag("correlation_id", "has spaces in it");
        sentryEvent.SetTag("kind", "request");

        var clean = SentryScrubber.Scrub(sentryEvent);

        Assert.Equal(["kind"], clean.Tags.Keys);
        Assert.Null(clean.TransactionName);
    }

    [Theory]
    [InlineData("Startup.InvalidSettings", true)]
    [InlineData("Could not connect to ayse@example.com", false)]
    [InlineData("Startup", false)]
    public void OnlyAnErrorCodeSurvivesAsAMessage(string message, bool kept)
    {
        var clean = SentryScrubber.Scrub(new SentryEvent { Message = new SentryMessage { Formatted = message } });

        Assert.Equal(kept ? message : null, clean.Message?.Message);
    }

    [Fact]
    public void AnExceptionTheSdkCaughtByItselfIsFiledUnderItsType()
    {
        var sentryEvent = new SentryEvent { SentryExceptions = [new SentryException { Type = "System.InvalidOperationException", Value = Email }] };

        var clean = SentryScrubber.Scrub(sentryEvent);

        Assert.Equal("System.InvalidOperationException", clean.Tags["error_code"]);
        Assert.Null(clean.SentryExceptions!.Single().Value);
    }

    [Fact]
    public void TheSdkSettingsSendNoPersonalDataAndOnlyErrors()
    {
        var options = new SentryOptions();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sentry:Dsn"] = "https://public@o1.ingest.de.sentry.io/1", ["Sentry:Release"] = "abc1234" })
            .Build();

        ErrorReporting.Configure(options, configuration, "Production");

        Assert.False(options.SendDefaultPii);
        Assert.False(options.IsEnvironmentUser);
        Assert.Equal("kombin-api", options.ServerName);
        Assert.Equal(0, options.MaxBreadcrumbs);
        Assert.False(options.AttachStacktrace);
        Assert.False(options.AutoSessionTracking);
        Assert.Equal(1.0f, options.SampleRate);
        Assert.Equal(0.0, options.TracesSampleRate);
        Assert.Equal("kombin-api@abc1234", options.Release);
        Assert.Equal("Production", options.Environment);
        Assert.False(options.SendClientReports);
        Assert.False(options.CaptureFailedRequests);
        Assert.True(options.DisableFileWrite);
    }

    /// <summary>
    /// The whole way out: the real SDK, started with the real settings and a transport that keeps the envelope instead of sending it. This is what
    /// would reach Sentry, headers and all, for an exception whose message, scope and hint carry personal data.
    /// </summary>
    [Fact]
    public async Task TheEnvelopeTheSdkWouldSendHoldsNothingPersonal()
    {
        var transport = new KeepingTransport();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sentry:Dsn"] = "https://public@o1.ingest.de.sentry.io/1", ["Sentry:Release"] = "abc1234" })
            .Build();

        using (SentrySdk.Init(options =>
        {
            ErrorReporting.Configure(options, configuration, "Production");
            options.Transport = transport;
        }))
        {
            SentrySdk.ConfigureScope(scope =>
            {
                scope.User = new SentryUser { Email = Email, IpAddress = Ip, Id = UserId };
                scope.SetExtra("body", Body);
                scope.AddBreadcrumb("GET " + PresignedUrl);
                scope.AddAttachment(Encoding.UTF8.GetBytes(Body), "request.json");
                scope.Request.Url = "https://api.kombin.app/x" + Query;
                scope.Request.Cookies = Cookie;
            });

            try
            {
                throw new InvalidOperationException(MessageText);
            }
            catch (InvalidOperationException exception)
            {
                ErrorReporting.Capture(exception, "request", "/api/v1/wardrobe/items/{id}", ErrorReporting.ErrorCode(exception), "0HN7ABC:00000001");
            }

            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
        }

        // Every envelope the SDK produced, whatever it is, is searched.
        var envelope = string.Join("\n", transport.Envelopes);
        Assert.Single(transport.Envelopes, sent => sent.Contains("\"type\":\"event\"", StringComparison.Ordinal));
        foreach (var value in Forbidden)
        {
            Assert.False(envelope.Contains(value, StringComparison.OrdinalIgnoreCase), $"'{value}' would leave the server: {envelope}");
        }

        Assert.Contains("System.InvalidOperationException", envelope, StringComparison.Ordinal);
        Assert.Contains("/api/v1/wardrobe/items/{id}", envelope, StringComparison.Ordinal);
        Assert.Contains("0HN7ABC:00000001", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("request.json", envelope, StringComparison.Ordinal);
    }

    private sealed class KeepingTransport : Sentry.Extensibility.ITransport
    {
        public List<string> Envelopes { get; } = [];

        public async Task SendEnvelopeAsync(Sentry.Protocol.Envelopes.Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            lock (Envelopes)
            {
                Envelopes.Add(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
    }

    [Fact]
    public void WithoutADsnNothingStarts()
    {
        var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sentry:Dsn"] = " " }).Build();

        Assert.False(ErrorReporting.IsEnabled(empty));
        Assert.Null(ErrorReporting.Start(empty, "Production"));
        Assert.False(SentrySdk.IsEnabled);
    }

    [Theory]
    [InlineData(nameof(Jobs.NoRetries), 0, true)]
    [InlineData(nameof(Jobs.ThreeRetries), 0, false)]
    [InlineData(nameof(Jobs.ThreeRetries), 2, false)]
    [InlineData(nameof(Jobs.ThreeRetries), 3, true)]
    [InlineData(nameof(Jobs.HangfireDefault), 9, false)]
    [InlineData(nameof(Jobs.HangfireDefault), 10, true)]
    public void AJobIsReportedOnlyWhenItWillNotBeRetried(string method, int retryCount, bool reported)
    {
        Assert.Equal(reported, JobFailureFilter.IsLastAttempt(typeof(Jobs).GetMethod(method)!, retryCount));
    }

    [Fact]
    public void AJobIsNamedByItsTypeAndMethodNeverItsArguments()
    {
        Assert.Equal("Jobs.ThreeRetries", JobFailureFilter.JobName(typeof(Jobs), typeof(Jobs).GetMethod(nameof(Jobs.ThreeRetries))!));
    }

    private static SentryEvent EventFullOfPersonalData()
    {
        var sentryEvent = new SentryEvent
        {
            Level = SentryLevel.Error,
            Release = "kombin-api@abc1234",
            Environment = "Production",
            ServerName = "the-machine",
            Logger = "Kombin.Api." + Email,
            Message = new SentryMessage { Formatted = MessageText },
            User = new SentryUser { Email = Email, Username = Name, IpAddress = Ip, Id = UserId },
            Request = new SentryRequest
            {
                Url = "https://api.kombin.app/api/v1/wardrobe/items/" + UserId,
                QueryString = Query,
                Method = "GET",
                Data = Body,
                Cookies = Cookie,
            },
            SentryExceptions =
            [
                new SentryException
                {
                    Type = "Npgsql.PostgresException",
                    Value = MessageText,
                    Mechanism = new Mechanism { Type = "generic", Handled = false, Description = MessageText, HelpLink = PresignedUrl },
                    Stacktrace = new SentryStackTrace
                    {
                        Frames =
                        [
                            new SentryStackFrame
                            {
                                Function = "Kombin.Api.Features.Wardrobe.GetItem.Endpoint.Handle",
                                LineNumber = 42,
                                FileName = @"C:\Users\ayse.yilmaz\src\Kombin.Api\Features\Wardrobe\GetItem\Endpoint.cs",
                                AbsolutePath = "/home/ayse.yilmaz/src/Endpoint.cs",
                                ContextLine = "var key = \"" + StorageKey + "\";",
                            },
                        ],
                    },
                },
            ],
        };

        sentryEvent.SentryExceptions!.Single().Mechanism!.Data["user"] = Email;
        sentryEvent.SentryExceptions!.Single().Stacktrace!.Frames[0].Vars["email"] = Email;
        sentryEvent.Request.Headers["Authorization"] = "Bearer " + Token;
        sentryEvent.Request.Headers["X-Forwarded-For"] = Ip;
        sentryEvent.Request.Env["REMOTE_ADDR"] = Ip;
        sentryEvent.SetTag("kind", "request");
        sentryEvent.SetTag("source", "/api/v1/wardrobe/items/{id}");
        sentryEvent.SetTag("error_code", "Npgsql.PostgresException");
        sentryEvent.SetTag("correlation_id", "0HN7ABC:00000001");
        sentryEvent.SetTag("user.email", Email);
        sentryEvent.SetTag("url", PresignedUrl);
        sentryEvent.SetExtra("body", Body);
        sentryEvent.SetExtra("key", StorageKey);
        sentryEvent.AddBreadcrumb(new Breadcrumb("GET " + PresignedUrl, "http"));
        sentryEvent.Contexts.Device.Name = "the-machine";
        sentryEvent.Contexts.Runtime.Name = ".NET";
        sentryEvent.DebugImages = [new DebugImage { CodeFile = @"C:\Users\ayse.yilmaz\Kombin.Api.dll" }];
        sentryEvent.Contexts["custom"] = new Dictionary<string, string> { ["email"] = Email };

        return sentryEvent;
    }

    private static string Serialize(SentryEvent sentryEvent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            sentryEvent.WriteTo(writer, null);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static class Jobs
    {
        [AutomaticRetry(Attempts = 0)]
        public static void NoRetries()
        {
        }

        [AutomaticRetry(Attempts = 3)]
        public static void ThreeRetries()
        {
        }

        public static void HangfireDefault()
        {
        }
    }
}
