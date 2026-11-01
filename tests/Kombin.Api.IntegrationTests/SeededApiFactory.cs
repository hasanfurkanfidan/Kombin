using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// An API host on its own empty PostgreSQL container with <c>Database:SeedOnStartup</c> on, so the seeding tests see exactly
/// what the seed file produces. Kept apart from <see cref="ApiFactory"/>, whose database every other test fills with its own data.
/// </summary>
public sealed class SeededApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Building the host runs migrations and the seed, so a broken seed file fails here instead of in the first test.
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

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Database:SeedOnStartup"] = "true",
            ["Jwt:Issuer"] = "kombin-tests",
            ["Jwt:Audience"] = "kombin-tests",
            ["Jwt:SigningKey"] = "integration-test-signing-key-9f8e7d6c5b4a3",
            ["Internal:ApiKey"] = ApiFactory.InternalApiKey,
            ["Serilog:MinimumLevel:Default"] = "Warning",
            // Tests call the jobs directly; no Hangfire server, recurring jobs or dashboard, and the hangfire schema is never created.
            ["Hangfire:Enabled"] = "false",
        }));
    }
}
