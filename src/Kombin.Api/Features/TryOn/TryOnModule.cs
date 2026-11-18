using Hangfire;
using Kombin.Api.Common.Hosting;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.TryOn.GetTryOn;
using Kombin.Api.Features.TryOn.Jobs;
using Kombin.Api.Features.TryOn.ReportTryOn;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.TryOn;

/// <summary>
/// "Manken üstünde gör" (design: <c>docs/design/try-on.md</c>): garments prepared by <c>tryon-prepare</c>, the readiness rule, cached renders and
/// "Yanlış görünüyor" reports. Off by default (<c>TryOn:Enabled</c>).
/// </summary>
internal static class TryOnModule
{
    public static IServiceCollection AddTryOnFeature(this IServiceCollection services)
    {
        services.AddOptions<TryOnOptions>().BindConfiguration(TryOnOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TryOnOptions>, TryOnOptionsValidator>();

        services.AddSingleton<TryOnRenderGate>();
        services.AddSingleton<TryOnCatalogueLock>();
        services.AddScoped<TryOnPreparer>();
        services.AddScoped<TryOnRenderer>();
        services.AddScoped<TryOnPrepareJob>();

        return services;
    }

    /// <summary>
    /// Always mapped (the OpenAPI document and the authorization matrix see the routes); with <c>TryOn:Enabled</c> false they answer 404
    /// <c>TryOn.Disabled</c> before anything else runs. New renders are limited by <see cref="TryOnRenderGate"/> (cache hits and checks are free); the routes also share the <c>tryon</c> rate-limit policy, which only stops a client in a loop.
    /// </summary>
    public static IEndpointRouteBuilder MapTryOnEndpoints(this IEndpointRouteBuilder app)
    {
        var tryOn = app.MapGroup("/api/v1/me/tryon")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.TryOn)
            .WithTags("TryOn")
            .AddEndpointFilter<RouteGroupBuilder, FeatureEnabledFilter<TryOnOptions>>();

        GetTryOnEndpoint.Map(tryOn);
        ReportTryOnEndpoint.Map(tryOn);

        return app;
    }

    /// <summary>
    /// <c>tryon-prepare</c> every minute, behind <c>Hangfire:Enabled</c> and <c>TryOn:Enabled</c> (removed when try-on is off). Called in
    /// <c>Program.cs</c> after <c>UseLeagueJobs</c>.
    /// </summary>
    public static void UseTryOnJobs(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(HangfireSettings.EnabledKey))
        {
            return;
        }

        var jobs = app.Services.GetRequiredService<IRecurringJobManager>();
        if (!app.Services.GetRequiredService<IOptions<TryOnOptions>>().Value.Enabled)
        {
            jobs.RemoveIfExists(TryOnPrepareJob.JobId);
            return;
        }

        jobs.AddOrUpdate<TryOnPrepareJob>(
            TryOnPrepareJob.JobId, job => job.RunAsync(CancellationToken.None), TryOnPrepareJob.Cron, new RecurringJobOptions { TimeZone = Istanbul.Zone });
    }
}
