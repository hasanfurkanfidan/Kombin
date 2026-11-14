using System.Text;
using System.Text.Json;
using Kombin.Api.Common.Hosting;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Features.Legal;
using Kombin.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Tests;

public class HostingTests
{
    [Fact]
    public void TheProxyNetworksMayBeOneCommaSeparatedValue()
    {
        var options = new ProxyOptions { KnownNetworks = ["172.16.0.0/12, 10.0.0.0/8", "192.168.0.0/16;fd00::/8", " "] };

        Assert.Equal(["172.16.0.0/12", "10.0.0.0/8", "192.168.0.0/16", "fd00::/8"], options.ParsedNetworks());
        Assert.Equal(["172.16.0.0/12"], new ProxyOptions().KnownNetworks);
        Assert.False(new ProxyOptions().TlsTerminated);
    }

    [Fact]
    public void TheLegalPagesShowPlaceholdersUntilTheControllerIsConfigured()
    {
        var blank = new LegalOptions();

        Assert.Equal("{{CONTROLLER_NAME}}", blank.ControllerNameOrPlaceholder);
        Assert.Equal("{{CONTACT_EMAIL}}", blank.ContactEmailOrPlaceholder);
        Assert.Equal("{{ADDRESS}}", blank.AddressOrPlaceholder);

        var filled = new LegalOptions { ControllerName = " Kombin A.Ş. ", ContactEmail = "a@b.c", Address = "İstanbul" };
        Assert.Equal("Kombin A.Ş.", filled.ControllerNameOrPlaceholder);
        Assert.Equal("a@b.c", filled.ContactEmailOrPlaceholder);
        Assert.Equal("İstanbul", filled.AddressOrPlaceholder);
    }

    [Fact]
    public void TheMigrateCommandIsTheFirstArgumentOnly()
    {
        Assert.True(MigrateCommand.IsRequested(["migrate"]));
        Assert.False(MigrateCommand.IsRequested([]));
        Assert.False(MigrateCommand.IsRequested(["--urls", "migrate"]));
        Assert.False(MigrateCommand.IsRequested(["Migrate"]));
    }

    [Fact]
    public void ErrorsCanCarryExtraMembersForTheProblemDocument()
    {
        var plain = Error.Forbidden("X.Y", "no");
        var extended = plain.WithExtension("date", "2026-11-21");

        Assert.Null(plain.Extensions);
        Assert.Equal("2026-11-21", extended.Extensions!["date"]);
        Assert.Equal("X.Y", extended.Code);
        Assert.Equal(ErrorType.Forbidden, extended.ErrorType);
    }

    [Fact]
    public async Task AnUnexpectedErrorHidesItsDetailOutsideDevelopment()
    {
        foreach (var (environment, expectDetail) in new[] { ("Production", false), ("Development", true) })
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
            var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance, new FakeEnvironment(environment));

            Assert.True(await handler.TryHandleAsync(context, new InvalidOperationException("secret connection detail"), CancellationToken.None));

            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
            Assert.Equal(expectDetail, body.Contains("secret connection detail", StringComparison.Ordinal));
            Assert.Contains("Server.UnexpectedError", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRateLimitDefaultsAreTheLaunchLimitsAndTheValidatorRefusesNonsense()
    {
        var defaults = new RateLimitOptions();

        Assert.True(defaults.Enabled);
        Assert.Equal((10, 300), (defaults.Auth.PermitLimit, defaults.Auth.WindowSeconds));
        Assert.Equal((20, 3600), (defaults.Lens.Anonymous.PermitLimit, defaults.Lens.Anonymous.WindowSeconds));
        Assert.Equal((60, 3600), (defaults.Lens.Authenticated.PermitLimit, defaults.Lens.Authenticated.WindowSeconds));
        Assert.Equal((60, 3600), (defaults.Uploads.PermitLimit, defaults.Uploads.WindowSeconds));
        Assert.Equal((600, 300), (defaults.Events.PermitLimit, defaults.Events.WindowSeconds));
        Assert.Equal((120, 60), (defaults.Go.PermitLimit, defaults.Go.WindowSeconds));
        Assert.Equal((600, 60), (defaults.Global.PermitLimit, defaults.Global.WindowSeconds));
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.Auth.Window);

        var validator = new RateLimitOptionsValidator();
        Assert.True(validator.Validate(null, defaults).Succeeded);

        var broken = new RateLimitOptions { Auth = new RateLimitWindow(0, 60), Go = new RateLimitWindow(5, 0) };
        var result = validator.Validate(null, broken);
        Assert.False(result.Succeeded);
        Assert.Contains("RateLimits:Auth:PermitLimit", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("RateLimits:Go:WindowSeconds", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestCountsAgainstTheUserWhenSignedInAndTheAddressOtherwise()
    {
        var anonymous = new DefaultHttpContext();
        anonymous.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        Assert.Null(RateLimitKeys.User(anonymous));
        Assert.Equal("203.0.113.9", RateLimitKeys.Client(anonymous));
        Assert.Equal("ip:203.0.113.9", RateLimitKeys.UserOrClient(anonymous));

        var noAddress = new DefaultHttpContext();
        Assert.Equal("unknown", RateLimitKeys.Client(noAddress));

        var userId = Guid.NewGuid();
        var signedIn = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", userId.ToString())], "Bearer")),
        };
        signedIn.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        Assert.Equal($"u:{userId:N}", RateLimitKeys.UserOrClient(signedIn));

        // A principal without a valid sub, or not authenticated, is anonymous.
        var badSub = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "nope")], "Bearer")),
        };
        Assert.Null(RateLimitKeys.User(badSub));
        var unauthenticated = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", userId.ToString())])),
        };
        Assert.Null(RateLimitKeys.User(unauthenticated));
    }

    private static RequestBodyLimitMiddleware Middleware(RequestDelegate next, RequestLimitsOptions? limits = null) =>
        new(next, Options.Create(limits ?? new RequestLimitsOptions()));

    private static DefaultHttpContext Request(string path, string? contentType, long? length, Endpoint? endpoint = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = length;
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();

        if (endpoint is not null)
        {
            context.SetEndpoint(endpoint);
        }

        return context;
    }

    [Theory]
    [InlineData("/api/v1/events", "application/json", 1_048_576L, true)]
    [InlineData("/api/v1/events", "application/json", 1_048_577L, false)]
    [InlineData("/api/v1/events", "application/json; charset=utf-8", 1_048_577L, false)]
    [InlineData("/api/v1/events", "application/octet-stream", 10_485_760L, true)]
    [InlineData("/api/v1/events", "application/octet-stream", 10_485_761L, false)]
    [InlineData("/api/v1/events", null, 20_000_000L, false)]
    [InlineData("/api/v1/events", "application/json", null, true)]
    [InlineData("/api/v1/internal/catalog/offers/upsert", "application/json", 5_000_000L, true)]
    [InlineData("/api/v1/internal/catalog/offers/upsert", "application/json", 10_485_761L, false)]
    public async Task TheLimitDependsOnTheBodyTypeAndTheRoute(string path, string? contentType, long? length, bool allowed)
    {
        var called = false;
        var context = Request(path, contentType, length);

        await Middleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }).InvokeAsync(context);

        Assert.Equal(allowed, called);
        Assert.Equal(allowed ? StatusCodes.Status200OK : StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task AnEndpointWithItsOwnLimitKeepsIt()
    {
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RequestSizeLimitAttribute(8_000_000)), "lens");

        var within = Request("/api/v1/lens/search", "multipart/form-data", 7_999_999, endpoint);
        var calledWithin = false;
        await Middleware(_ =>
        {
            calledWithin = true;
            return Task.CompletedTask;
        }).InvokeAsync(within);
        Assert.True(calledWithin);

        var beyond = Request("/api/v1/lens/search", "multipart/form-data", 8_000_001, endpoint);
        var calledBeyond = false;
        await Middleware(_ =>
        {
            calledBeyond = true;
            return Task.CompletedTask;
        }).InvokeAsync(beyond);
        Assert.False(calledBeyond);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, beyond.Response.StatusCode);

        // The explicit limit also wins over the smaller JSON one.
        var json = Request("/api/v1/lens/search", "application/json", 3_000_000, endpoint);
        var calledJson = false;
        await Middleware(_ =>
        {
            calledJson = true;
            return Task.CompletedTask;
        }).InvokeAsync(json);
        Assert.True(calledJson);
    }

    [Fact]
    public async Task TheRefusalIsAProblemDocumentWithAStableCode()
    {
        var context = Request("/api/v1/events", "application/json", 2_000_000);

        await Middleware(_ => Task.CompletedTask).InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Request.TooLarge", body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(413, body.RootElement.GetProperty("status").GetInt32());
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Kombin.Api";

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
