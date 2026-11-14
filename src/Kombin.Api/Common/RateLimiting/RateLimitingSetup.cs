using System.Globalization;
using System.Threading.RateLimiting;
using Kombin.Api.Common.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.RateLimiting;

/// <summary>The names endpoints ask for with <c>RequireRateLimiting</c>.</summary>
internal static class RateLimitPolicies
{
    public const string Auth = "auth";

    public const string Lens = "lens";

    public const string Uploads = "uploads";

    public const string Events = "events";

    public const string Go = "go";
}

/// <summary>Who a request counts against: the signed-in user's ID, else the client address (after forwarded headers, so the real one behind the proxy).</summary>
internal static class RateLimitKeys
{
    public const string Unknown = "unknown";

    public static string Client(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? Unknown;

    public static string? User(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true && Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId)
            ? userId.ToString("N", CultureInfo.InvariantCulture)
            : null;

    /// <summary><c>u:{user}</c> when signed in, else <c>ip:{address}</c>.</summary>
    public static string UserOrClient(HttpContext context) => User(context) is { } user ? "u:" + user : "ip:" + Client(context);
}

/// <summary>
/// ASP.NET Core's built-in rate limiter, in memory (per instance), fixed windows. Policies: <c>auth</c> (per IP), <c>lens</c> (per user, else per
/// IP, different limits), <c>uploads</c>, <c>events</c> (per user, else per IP), <c>go</c> (per IP) and a global fallback (per IP) under all of
/// them. A rejected request is a 429 problem document with <c>errorCode: "RateLimited"</c> and a <c>Retry-After</c> header. Limits are read from
/// <see cref="RateLimitOptions"/> when a caller's limiter is first created, so configuration applied after Program.cs still wins.
/// </summary>
internal static class RateLimitingSetup
{
    public const string ErrorCode = "RateLimited";

    public static IServiceCollection AddKombinRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>().BindConfiguration(RateLimitOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var limits = LimitsOf(context);

                // Probes (the Docker health check, a load balancer) must never be refused.
                return !limits.Enabled || context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                    ? RateLimitPartition.GetNoLimiter("exempt")
                    : FixedWindow("global:" + RateLimitKeys.Client(context), limits.Global);
            });

            options.AddPolicy(RateLimitPolicies.Auth, context => Policy(context, "auth:" + RateLimitKeys.Client(context), limits => limits.Auth));
            options.AddPolicy(RateLimitPolicies.Go, context => Policy(context, "go:" + RateLimitKeys.Client(context), limits => limits.Go));
            options.AddPolicy(RateLimitPolicies.Uploads, context => Policy(context, "uploads:" + RateLimitKeys.UserOrClient(context), limits => limits.Uploads));
            options.AddPolicy(RateLimitPolicies.Events, context => Policy(context, "events:" + RateLimitKeys.UserOrClient(context), limits => limits.Events));
            options.AddPolicy(
                RateLimitPolicies.Lens,
                context => Policy(
                    context,
                    "lens:" + RateLimitKeys.UserOrClient(context),
                    limits => RateLimitKeys.User(context) is null ? limits.Lens.Anonymous : limits.Lens.Authenticated));
        });

        return services;
    }

    private static RateLimitOptions LimitsOf(HttpContext context) => context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

    private static RateLimitPartition<string> Policy(HttpContext context, string key, Func<RateLimitOptions, RateLimitWindow> window)
    {
        var limits = LimitsOf(context);

        return limits.Enabled ? FixedWindow(key, window(limits)) : RateLimitPartition.GetNoLimiter("off");
    }

    private static RateLimitPartition<string> FixedWindow(string key, RateLimitWindow window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = window.PermitLimit,
            Window = window.Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;

        // The limiter knows when the window ends; fall back to a minute if it did not say.
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))
            : 60;

        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        await Problems.Create(
                http,
                StatusCodes.Status429TooManyRequests,
                title: "Too many requests.",
                detail: "Too many requests. Try again after the time in the Retry-After header.",
                errorCode: ErrorCode,
                extra: new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfter })
            .ExecuteAsync(http);
    }
}
