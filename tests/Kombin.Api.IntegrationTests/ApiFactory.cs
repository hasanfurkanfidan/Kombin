using Kombin.Api.Features.Notification;
using Microsoft.AspNetCore.Hosting;
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

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    /// <summary>The Expo push service as the jobs see it. It answers by token (see <see cref="FakeExpoPushClient"/>), so tests running in parallel do not disturb each other.</summary>
    internal FakeExpoPushClient Expo => Services.GetRequiredService<FakeExpoPushClient>();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Build the host (and apply migrations) once, up front. Otherwise test classes running in parallel each
        // trigger the lazy startup and race to migrate the same database.
        _ = Services;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
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
        }));
    }
}
