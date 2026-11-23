using Hangfire;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Vision;
using Kombin.Api.Features.Admin;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Features.Identity.ExternalTokens;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Features.Wardrobe;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Kombin.Api.IntegrationTests.ApiFactory))]

namespace Kombin.Api.IntegrationTests;

/// <summary>One real PostgreSQL container and one API host shared by every integration test in the assembly.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string InternalApiKey = "integration-test-internal-key-3c1d5e7f9a2b4d6e";

    /// <summary>The admin panel's only configured account in the tests (<c>Admin:Users:0</c>).</summary>
    public const string AdminEmail = "founder@kombin.test";

    public const string AdminPassword = "Admin-test-passw0rd!";

    // The same image as compose: PostgreSQL 18 with the pgvector extension the Wardrobe migration creates.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg18").Build();

    /// <summary>The Expo push service as the jobs see it. It answers by token (see <see cref="FakeExpoPushClient"/>), so tests running in parallel do not disturb each other.</summary>
    internal FakeExpoPushClient Expo => Services.GetRequiredService<FakeExpoPushClient>();

    /// <summary>The object store (in memory, not MinIO; see <see cref="FakeObjectStorage"/>).</summary>
    internal FakeObjectStorage Storage => Services.GetRequiredService<FakeObjectStorage>();

    /// <summary>What was enqueued for Hangfire; the tests run the jobs themselves.</summary>
    internal RecordingJobClient Jobs => Services.GetRequiredService<RecordingJobClient>();

    /// <summary>The shared test database, for tests that hold a lock of their own beside the API's requests.</summary>
    internal string ConnectionString => _postgres.GetConnectionString();

    // One PBKDF2 hash for the whole run: hashing is deliberately slow.
    private static readonly string AdminPasswordHash = AdminHashCommand.Hash(AdminPassword);

    // Set once the host has been built. When start-up fails (the container, the migrations, the host), the fixture is still disposed, and
    // WebApplicationFactory.DisposeAsync throws a NullReferenceException on a host that never existed, which buries the real error.
    private bool _hostBuilt;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Build the host (and apply migrations) once, up front. Otherwise test classes running in parallel each
        // trigger the lazy startup and race to migrate the same database.
        _ = Services;
        _hostBuilt = true;
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_hostBuilt)
            {
                await base.DisposeAsync();
            }
        }
        finally
        {
            await _postgres.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IExpoPushClient>();
            services.AddSingleton<FakeExpoPushClient>();
            services.AddSingleton<IExpoPushClient>(provider => provider.GetRequiredService<FakeExpoPushClient>());

            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<FakeObjectStorage>();
            services.AddSingleton<IObjectStorage>(provider => provider.GetRequiredService<FakeObjectStorage>());

            services.RemoveAll<IVisionClient>();
            services.AddSingleton<IVisionClient, FakeVisionClient>();

            services.RemoveAll<IBackgroundJobClient>();
            services.AddSingleton<RecordingJobClient>();
            services.AddSingleton<IBackgroundJobClient>(provider => provider.GetRequiredService<RecordingJobClient>());

            services.AddHttpClient(WardrobeModule.CatalogImageClient).ConfigurePrimaryHttpMessageHandler(() => new FakeCatalogImageHandler());
            services.AddHttpClient(ProductEmbeddingJob.ImageClient).ConfigurePrimaryHttpMessageHandler(() => new FakeCatalogImageHandler());

            // The outbound guard above the fake handlers stays real; only its DNS is faked (test hosts do not resolve).
            services.RemoveAll<IOutboundResolver>();
            services.AddSingleton<IOutboundResolver, FakeOutboundResolver>();

            services.RemoveAll<IExternalTokenVerifier>();
            services.AddSingleton<IExternalTokenVerifier, FakeExternalTokenVerifier>();

            services.RemoveAll<IWeatherProvider>();
            services.AddSingleton<IWeatherProvider, FakeWeatherProvider>();

            // The in-memory server has no client address; the admin login throttle keys on it, so a test picks its own with a header.
            services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>();

            // The real clock, unless a request names its own time (X-Test-Now): the league tests play days of their own, at any hour.
            services.AddHttpContextAccessor();
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock, TestHeaderClock>();

            // Under a shifted test clock (TestClock), tokens are minted at the shifted time, so their lifetime is checked against it too.
            if (TestClock.IsShifted)
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    options.TokenValidationParameters.LifetimeValidator = (notBefore, expires, _, parameters) =>
                    {
                        var now = TestClock.UtcNow.UtcDateTime;
                        return (notBefore is null || notBefore.Value <= now + parameters.ClockSkew) && (expires is null || expires.Value >= now - parameters.ClockSkew);
                    });
            }
        });

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            // appsettings.Development.json turns seeding on; the shared database must only hold what each test creates.
            ["Database:SeedOnStartup"] = "false",
            ["Jwt:Issuer"] = "kombin-tests",
            ["Jwt:Audience"] = "kombin-tests",
            ["Jwt:SigningKey"] = "integration-test-signing-key-9f8e7d6c5b4a3",
            ["Internal:ApiKey"] = InternalApiKey,
            ["Serilog:MinimumLevel:Default"] = "Warning",
            // Tests call the jobs directly; no Hangfire server, recurring jobs or dashboard, and the hangfire schema is never created.
            ["Hangfire:Enabled"] = "false",
            // The embedding job's one-request-per-host-per-second pace would make every test wait; PoliteScheduleTests pin the rule.
            ["CatalogEmbeddings:HostInterval"] = "00:00:00",
            // The limits (and the global fallback) are off: the shared host serves hundreds of requests from one in-memory address. The
            // rate-limit tests build their own host with them on and tiny limits.
            ["RateLimits:Enabled"] = "false",
            ["Legal:ControllerName"] = "Kombin Test A.Ş.",
            ["Legal:ContactEmail"] = "privacy@kombin.test",
            ["Legal:Address"] = "Test Mah. 1. Sk. No:1 İstanbul",
            ["Admin:Users:0:Email"] = AdminEmail,
            ["Admin:Users:0:PasswordHash"] = AdminPasswordHash,
            // League:Enabled is false by default (until the app screens ship); the tests switch it on. LeagueTests checks the switched-off host.
            ["League:Enabled"] = "true",
            ["AskFriend:Enabled"] = "true",
            ["AskFriend:PublicBaseUrl"] = "https://api.kombin.test",
            // TryOn:Enabled is false by default too; the tests switch it on (TryOnTests checks the switched-off host).
            ["TryOn:Enabled"] = "true",
            // Bulk add is off by default too; the tests switch it on (BulkAddDisabledTests checks the switched-off host). A busy vision service is
            // asked again after no wait at all: BulkAddRulesTests pin the clamp, and the integration tests must not sleep.
            ["BulkAdd:Enabled"] = "true",
            ["BulkAdd:BusyRetryMin"] = "00:00:00",
            ["BulkAdd:BusyRetryMax"] = "00:00:00",
        }));
    }
}

/// <summary>Lets a test set <c>HttpContext.Connection.RemoteIpAddress</c> with the <c>X-Test-Remote-Ip</c> header.</summary>
internal sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var value) && System.Net.IPAddress.TryParse(value.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return nextMiddleware(context);
        });

        next(app);
    };
}

/// <summary>
/// The tests' clock (<see cref="TestClock"/>: the system clock unless a run shifts it), except for a request carrying <c>X-Test-Now</c> (an
/// ISO 8601 instant): its handler, filters and validators all see that time. A test whose outcome depends on the time of day or the date sends it
/// (a league day closes at 21:00 Istanbul, a wear's default day is today in Istanbul); jobs that tests call directly take their time as a parameter.
/// </summary>
internal sealed class TestHeaderClock(IHttpContextAccessor accessor) : IClock
{
    public const string Header = "X-Test-Now";

    public DateTimeOffset UtcNow =>
        accessor.HttpContext?.Request.Headers.TryGetValue(Header, out var value) == true
            && DateTimeOffset.TryParse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var now)
            ? now.ToUniversalTime()
            : TestClock.UtcNow;
}
