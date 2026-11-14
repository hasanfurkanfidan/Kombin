using Hangfire.Dashboard;
using Kombin.Api.Common.Http;
using Kombin.Api.Features.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.Hosting;

/// <summary>What production adds around the app: Kestrel limits, forwarded headers, TLS-terminated transport, persisted data-protection keys, diagnostics.</summary>
internal static class HostingExtensions
{
    public const string DiagnosticsExposeKey = "Diagnostics:Expose";

    public const string DataProtectionKeysPathKey = "DataProtection:KeysPath";

    /// <summary>Where the compose image keeps the data-protection keys (a named volume); production uses it unless configured otherwise.</summary>
    public const string DefaultProductionKeysPath = "/keys";

    public static WebApplicationBuilder AddKombinHosting(this WebApplicationBuilder builder)
    {
        var limits = builder.Configuration.GetSection(RequestLimitsOptions.SectionName).Get<RequestLimitsOptions>() ?? new RequestLimitsOptions();

        // Kestrel's own limits are the last line: the middleware refuses by Content-Length first and sets the per-request body limit.
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = limits.DefaultBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            kestrel.Limits.MaxRequestLineSize = 8 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        });

        builder.Services.AddOptions<RequestLimitsOptions>().BindConfiguration(RequestLimitsOptions.SectionName);
        builder.Services.AddOptions<ProxyOptions>().BindConfiguration(ProxyOptions.SectionName);

        // Configured lazily from ProxyOptions so overrides applied after Program.cs (tests, hosting) still count.
        builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ProxyOptions>>((forwarded, proxy) =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // One proxy in front: the last X-Forwarded-For entry is the client the proxy saw; anything a client prepends is ignored.
            forwarded.ForwardLimit = 1;
            forwarded.RequireHeaderSymmetry = false;

            foreach (var network in proxy.Value.ParsedNetworks())
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        builder.Services.AddHsts(hsts =>
        {
            hsts.MaxAge = TimeSpan.FromDays(365);
            hsts.IncludeSubDomains = true;
        });
        builder.Services.AddHttpsRedirection(redirection =>
        {
            redirection.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            redirection.HttpsPort = 443;
        });

        // The keys behind the admin cookie and the antiforgery tokens: without persisting them a restart signs the founder out. Production
        // keeps them in /keys (a volume) unless DataProtection:KeysPath says otherwise; elsewhere they stay where the framework puts them.
        // Configured lazily (like PersistKeysToFileSystem does) so configuration applied after Program.cs still counts.
        builder.Services.AddDataProtection().SetApplicationName("kombin");
        builder.Services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, IHostEnvironment, ILoggerFactory>((keys, configuration, environment, loggers) =>
        {
            var keysPath = configuration[DataProtectionKeysPathKey] ?? (environment.IsProduction() ? DefaultProductionKeysPath : null);

            if (!string.IsNullOrWhiteSpace(keysPath))
            {
                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(keysPath), loggers);
            }
        });

        // In Production the app refuses to start with dev defaults (see ProductionSettings); a no-op in every other environment.
        builder.Services.AddOptions<ProductionSettingsCheck>().ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ProductionSettingsCheck>, ProductionSettingsValidator>();

        return builder;
    }

    /// <summary>
    /// Runs the Production settings check now and prints what is wrong to <paramref name="error"/>. True when the app may go on (always, outside
    /// Production). Called right after the host is built so a misconfigured Production start fails before any database work.
    /// </summary>
    public static bool HasValidProductionSettings(this WebApplication app, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            _ = app.Services.GetRequiredService<IOptions<ProductionSettingsCheck>>().Value;

            return true;
        }
        catch (OptionsValidationException exception)
        {
            error.WriteLine(exception.Message);

            return false;
        }
    }

    /// <summary>
    /// First in the pipeline: from a proxy in <c>Proxy:KnownNetworks</c> (or loopback) the real client address and scheme replace the
    /// proxy's, for the request log, the rate limiter, the admin login brake and the HTTPS redirect.
    /// </summary>
    public static IApplicationBuilder UseKombinForwardedHeaders(this IApplicationBuilder app) => app.UseForwardedHeaders();

    /// <summary>HSTS and the HTTPS redirect, only when <c>Proxy:TlsTerminated</c> is true (the proxy then sets <c>X-Forwarded-Proto</c>).</summary>
    public static IApplicationBuilder UseKombinTransportSecurity(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value.TlsTerminated)
        {
            // HSTS is a promise about a real domain: not made in Development (the closed-beta tunnel), where only the redirect applies.
            if (!app.Environment.IsDevelopment())
            {
                app.UseHsts();
            }

            app.UseHttpsRedirection();
        }

        return app;
    }

    /// <summary>OpenAPI and the Hangfire dashboard: Development, or any environment that sets <c>Diagnostics:Expose</c>.</summary>
    public static bool ExposesDiagnostics(this WebApplication app) =>
        app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>(DiagnosticsExposeKey);

    /// <summary>
    /// The Hangfire dashboard's access rule. Development: open (the compose stack reaches it from outside the container). Elsewhere, when
    /// diagnostics are exposed on purpose, it needs the founder's admin sign-in (the <c>/admin</c> cookie).
    /// </summary>
    public sealed class DashboardAuthorization(bool open) : IDashboardAsyncAuthorizationFilter
    {
        public async Task<bool> AuthorizeAsync(DashboardContext context)
        {
            if (open)
            {
                return true;
            }

            var result = await context.GetHttpContext().AuthenticateAsync(AdminAuth.Scheme);

            return result.Succeeded;
        }
    }
}
