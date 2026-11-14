using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;

namespace Kombin.Api.Features.Admin;

/// <summary>Names the admin panel's authentication: its own cookie scheme and policy, nothing shared with the app's JWT.</summary>
internal static class AdminAuth
{
    public const string Scheme = "KombinAdmin";

    public const string Policy = "KombinAdmin";

    public const string CookieName = "kombin_admin";

    public const string AntiforgeryCookieName = "kombin_admin_af";

    public const string PathPrefix = "/admin";

    public const string LoginPath = "/admin/login";

    /// <summary>8 hours, sliding: every request in the second half of the window renews it.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public static ClaimsPrincipal CreatePrincipal(string email)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, email), new Claim(ClaimTypes.NameIdentifier, email), new Claim("kombin:admin", "true")],
            Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }
}

internal static class AdminModule
{
    public static IServiceCollection AddAdminFeature(this IServiceCollection services)
    {
        services.AddOptions<AdminOptions>().BindConfiguration(AdminOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminOptions>, AdminOptionsValidator>();

        services.AddSingleton<AdminLoginThrottle>();
        services.AddScoped<AdminCredentials>();
        services.AddScoped<AdminAuditLog>();
        services.AddScoped<AdminDashboard>();

        // A second authentication scheme beside the JWT bearer one, which stays the default: the app's endpoints never see the admin cookie, and
        // the admin pages ask for it by name (the policy below), so an app user's token opens nothing here.
        services.AddAuthentication().AddCookie(AdminAuth.Scheme, options =>
        {
            options.Cookie.Name = AdminAuth.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = AdminAuth.SessionLifetime;
            options.SlidingExpiration = true;
            options.LoginPath = AdminAuth.LoginPath;
            options.AccessDeniedPath = AdminAuth.LoginPath;
            options.ReturnUrlParameter = "returnUrl";
        });

        // Secure outside Development (the panel must be served over HTTPS there); Development runs over plain HTTP on localhost.
        services.AddOptions<CookieAuthenticationOptions>(AdminAuth.Scheme)
            .Configure<IHostEnvironment>((options, environment) =>
                options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);

        services.AddAuthorizationBuilder().AddPolicy(AdminAuth.Policy, policy => policy
            .AddAuthenticationSchemes(AdminAuth.Scheme)
            .RequireAuthenticatedUser());

        services.AddAntiforgery();
        services.AddOptions<AntiforgeryOptions>().Configure<IHostEnvironment>((options, environment) =>
        {
            options.Cookie.Name = AdminAuth.AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.FormFieldName = "__AdminRequestVerificationToken";
        });

        // The pages are Turkish and full of "₺", "×" and "·": print them as they are instead of as &#x20BA; (still escaping < > & and quotes).
        services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

        services.AddRazorPages(options =>
        {
            // The pages live in Features/Admin/Pages (a feature folder, like everything else) and name their own /admin routes.
            options.RootDirectory = "/Features/Admin/Pages";
            options.Conventions.AuthorizeFolder("/", AdminAuth.Policy);
            options.Conventions.AllowAnonymousToPage("/Login");
        });

        return services;
    }

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapRazorPages();

        return app;
    }

    /// <summary>
    /// Headers for everything under <c>/admin</c>: never cached (the pages show personal data and signed image URLs), not framed, no referrer.
    /// The CSP lets the page load images from the object store and run its few inline lines; nothing else comes from elsewhere.
    /// </summary>
    public static IApplicationBuilder UseAdminSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(AdminAuth.PathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    headers.CacheControl = "no-store";
                    headers["X-Frame-Options"] = "DENY";
                    headers["X-Content-Type-Options"] = "nosniff";
                    headers["Referrer-Policy"] = "no-referrer";
                    headers.ContentSecurityPolicy =
                        "default-src 'none'; img-src 'self' data: http: https:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

                    return Task.CompletedTask;
                });
            }

            await next(context);
        });

    /// <summary>The address the login throttle keys on; a request without one (the in-memory test server) shares the key "unknown".</summary>
    public static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
