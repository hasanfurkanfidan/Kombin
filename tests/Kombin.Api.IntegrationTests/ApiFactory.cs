using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Kombin.Api.IntegrationTests.ApiFactory))]

namespace Kombin.Api.IntegrationTests;

/// <summary>One real PostgreSQL container and one API host shared by every integration test in the assembly.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

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
            ["Jwt:Issuer"] = "kombin-tests",
            ["Jwt:Audience"] = "kombin-tests",
            ["Jwt:SigningKey"] = "integration-test-signing-key-9f8e7d6c5b4a3",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        }));
    }
}
