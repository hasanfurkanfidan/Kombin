using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Kombin.Api.Features.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The app as <c>ASPNETCORE_ENVIRONMENT=Production</c> runs it: it refuses dev defaults and lists every problem, and with proper settings it
/// hides OpenAPI and the job dashboard, speaks no CORS, trusts forwarded headers only from the proxy network, redirects plain HTTP when TLS
/// ends at the proxy and keeps its data-protection keys on disk.
/// </summary>
public sealed class ProductionHostTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Proxy = "172.18.0.5";

    private static string Secret(int bytes = 48) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    private string? _superuserConnection;

    /// <summary>The test container's own password is "postgres", which the Production check rightly calls a default: use a role with a generated one.</summary>
    private async Task<string> StrongConnectionStringAsync()
    {
        if (_superuserConnection is not null)
        {
            return _superuserConnection;
        }

        var builder = new NpgsqlConnectionStringBuilder(factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres"));
        var role = $"prodtest_{Guid.NewGuid():N}";
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

        // Hex digits only, so nothing needs quoting.
        await using (var connection = new NpgsqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand($"CREATE ROLE {role} LOGIN SUPERUSER PASSWORD '{password}'", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        builder.Username = role;
        builder.Password = password;

        return _superuserConnection = builder.ConnectionString;
    }

    private async Task<Dictionary<string, string?>> ValidSettingsAsync(string keysPath) => new()
    {
        ["ConnectionStrings:Postgres"] = await StrongConnectionStringAsync(),
        ["Jwt:SigningKey"] = Secret(),
        ["Internal:ApiKey"] = Secret(),
        ["Vision:InternalKey"] = Secret(),
        ["Outbound:IpSalt"] = Secret(),
        ["Storage:Endpoint"] = "http://minio:9000",
        ["Storage:Bucket"] = "kombin",
        ["Storage:AccessKey"] = "kombin-app",
        ["Storage:SecretKey"] = Secret(),
        ["Storage:PublicBaseUrl"] = "https://storage.kombin.test/kombin",
        ["CatalogEmbeddings:BotContact"] = "bot@kombin.test",
        ["Database:MigrateOnStartup"] = "false",
        ["DataProtection:KeysPath"] = keysPath,
    };

    private static string NewKeysPath() => Path.Combine(Path.GetTempPath(), $"kombin-keys-{Guid.NewGuid():N}");

    private async Task<WebApplicationFactory<Program>> ProductionAsync(Dictionary<string, string?>? extra = null, string? keysPath = null)
    {
        var settings = await ValidSettingsAsync(keysPath ?? NewKeysPath());

        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });
    }

    private static HttpClient ClientFrom(WebApplicationFactory<Program> host, string? remote = null, bool https = false)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri(https ? "https://api.kombin.test" : "http://api.kombin.test"),
        });

        if (remote is not null)
        {
            client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.Header, remote);
        }

        return client;
    }

    [Fact]
    public async Task ItRefusesToStartWithDevelopmentDefaultsAndListsEveryProblemAtOnce()
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "dev-only-signing-key-change-me-0123456789abcdef",
                ["Internal:ApiKey"] = "dev-only-internal-key-change-me-0123456789abcdef",
                ["Outbound:IpSalt"] = "dev-only-ip-salt-change-me-0123456789abcdef",
                ["Vision:InternalKey"] = "",
                ["Storage:Endpoint"] = "http://localhost:9000",
                ["Storage:Bucket"] = "kombin",
                ["Storage:AccessKey"] = "kombin",
                ["Storage:SecretKey"] = "kombin_dev_minio_password",
                ["Storage:PublicBaseUrl"] = "http://localhost:9000/kombin",
                ["Legal:ControllerName"] = "",
                ["Legal:ContactEmail"] = "",
                ["Legal:Address"] = "",
                ["Admin:Users:0:Email"] = "",
                ["Admin:Users:0:PasswordHash"] = "",
                ["Database:MigrateOnStartup"] = "false",
            }));
        });

        // Program prints the problems to standard error and returns instead of serving, so there is no server to ask for a client.
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);

        try
        {
            Assert.ThrowsAny<Exception>(() => host.CreateClient());
        }
        finally
        {
            Console.SetError(original);
        }

        var message = captured.ToString();
        Assert.Contains("Refusing to start in Production", message, StringComparison.Ordinal);
        foreach (var key in new[] { "Jwt:SigningKey", "Internal:ApiKey", "Outbound:IpSalt", "Vision:InternalKey", "Storage:SecretKey", "Storage:PublicBaseUrl", "Legal:ControllerName", "Legal:ContactEmail", "Legal:Address", "Admin:Users", "database password" })
        {
            Assert.Contains(key, message, StringComparison.Ordinal);
        }

        // Never the values themselves.
        Assert.DoesNotContain("0123456789abcdef", message, StringComparison.Ordinal);
        Assert.DoesNotContain("kombin_dev_minio_password", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithGeneratedSettingsItStartsAndShowsNothingForDiagnostics()
    {
        using var host = await ProductionAsync();
        using var client = ClientFrom(host);

        using var live = await client.GetAsync("/health/live", Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var ready = await client.GetAsync("/health", Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var legal = await client.GetAsync("/api/v1/legal", Ct);
        Assert.Equal(HttpStatusCode.OK, legal.StatusCode);

        // Not exposed in Production, so these are plain 404s.
        using var openApi = await client.GetAsync("/openapi/v1.json", Ct);
        Assert.Equal(HttpStatusCode.NotFound, openApi.StatusCode);
        using var dashboard = await client.GetAsync("/hangfire", Ct);
        Assert.Equal(HttpStatusCode.NotFound, dashboard.StatusCode);
    }

    [Fact]
    public async Task DiagnosticsAppearOnlyWhenExposedOnPurpose()
    {
        using var host = await ProductionAsync(new() { ["Diagnostics:Expose"] = "true" });
        using var client = ClientFrom(host);

        using var openApi = await client.GetAsync("/openapi/v1.json", Ct);

        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
    }

    [Fact]
    public async Task CorsIsClosedSoAPreflightGetsNoPermission()
    {
        using var host = await ProductionAsync();
        using var client = ClientFrom(host);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/catalog/brands");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await client.SendAsync(request, Ct);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
        using var plain = await client.GetAsync("/api/v1/catalog/brands", Ct);
        Assert.False(plain.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task WhenTlsEndsAtTheProxyPlainHttpIsRedirectedAndHttpsGetsHsts()
    {
        using var host = await ProductionAsync(new() { ["Proxy:TlsTerminated"] = "true" });

        // Straight over HTTP (no proxy header): redirected to the HTTPS address.
        using (var plain = ClientFrom(host))
        {
            using var response = await plain.GetAsync("/health/live", Ct);
            Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
            Assert.Equal("https://api.kombin.test/health/live", response.Headers.Location?.ToString());
        }

        // Through the proxy, which says the client used HTTPS: served, with HSTS.
        using var viaProxy = ClientFrom(host, Proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var served = await viaProxy.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var hsts = Assert.Single(served.Headers.GetValues("Strict-Transport-Security"));
        Assert.Contains("max-age=31536000", hsts, StringComparison.Ordinal);

        // A forged header from anywhere else is not believed.
        using var forged = ClientFrom(host, "8.8.8.8");
        using var forgedRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        forgedRequest.Headers.Add("X-Forwarded-Proto", "https");
        using var refused = await forged.SendAsync(forgedRequest, Ct);
        Assert.Equal(HttpStatusCode.PermanentRedirect, refused.StatusCode);
    }

    [Fact]
    public async Task WithoutTlsTerminationThereIsNoRedirectAndNoHsts()
    {
        using var host = await ProductionAsync();
        using var client = ClientFrom(host);

        using var response = await client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task TheDataProtectionKeysAreWrittenToTheConfiguredFolder()
    {
        var keys = NewKeysPath();
        using var host = await ProductionAsync(keysPath: keys);
        using var client = ClientFrom(host, https: true);

        // The admin login form needs an antiforgery token, which is the first thing that asks for a key.
        using var response = await client.GetAsync("/admin/login", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));
    }

    [Fact]
    public async Task TheAdminLoginBrakeKeysOnTheForwardedClientAddress()
    {
        using var host = await ProductionAsync();
        using var client = ClientFrom(host, Proxy, https: true);

        async Task<HttpStatusCode> LoginFromAsync(string clientAddress, string password)
        {
            // The antiforgery cookie is Secure outside Development, hence the https base address; the token is read from the form.
            using var form = await client.GetAsync("/admin/login", Ct);
            var token = AdminBrowser.TokenIn(await form.Content.ReadAsStringAsync(Ct));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/login")
            {
                Content = new FormUrlEncodedContent(
                [
                    new("Input.Email", ApiFactory.AdminEmail),
                    new("Input.Password", password),
                    new("__AdminRequestVerificationToken", token),
                ]),
            };
            request.Headers.Add("X-Forwarded-For", clientAddress);
            using var response = await client.SendAsync(request, Ct);

            return response.StatusCode;
        }

        for (var attempt = 0; attempt < AdminLoginThrottle.MaxFailures; attempt++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await LoginFromAsync("203.0.113.40", "Wrong-password-1"));
        }

        // The forwarded client is locked out, even with the right password; another client behind the same proxy is not.
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFromAsync("203.0.113.40", ApiFactory.AdminPassword));
        Assert.Equal(HttpStatusCode.Redirect, await LoginFromAsync("203.0.113.41", ApiFactory.AdminPassword));
    }
}
