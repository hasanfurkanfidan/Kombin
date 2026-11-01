using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Features.Analytics.GetWeeklyMetrics;
using Kombin.Api.Features.Analytics.Jobs;
using Kombin.Api.Features.Analytics.RecordEvents;
using Kombin.Api.Features.Notification;

namespace Kombin.Api.Features.Analytics;

internal static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsFeature(this IServiceCollection services)
    {
        services.AddScoped<IAnalyticsIdentityLinker, AnalyticsIdentityLinker>();

        // A plain class with constructor injection, like the other jobs: Hangfire activates it from a scope, the tests resolve it directly.
        services.AddScoped<WeeklyMetricsJob>();

        return services;
    }

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: the app reports before login. A bearer token, when sent, attributes the events to the user.
        var events = app.MapGroup("/api/v1/events").WithTags("Analytics");
        RecordEventsEndpoint.Map(events);

        // Internal: for operators and dashboards, with X-Internal-Key.
        var internalAnalytics = app.MapGroup("/api/v1/internal/analytics").WithTags("Analytics (internal)").RequireInternalKey();
        GetWeeklyMetricsEndpoint.Map(internalAnalytics);

        return app;
    }

    /// <summary>
    /// Registers the recurring analytics job. Call it after <c>UseNotificationJobs</c> and the database migrations; like that method it does
    /// nothing unless <c>Hangfire:Enabled</c> is true, so tests and one-off tooling never touch the job tables.
    /// </summary>
    public static void UseAnalyticsJobs(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(NotificationModule.HangfireEnabledKey))
        {
            return;
        }

        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<WeeklyMetricsJob>(
            WeeklyMetricsJob.JobId,
            job => job.RunAsync(CancellationToken.None),
            WeeklyMetricsJob.Cron,
            new RecurringJobOptions { TimeZone = QuietHours.Zone });
    }
}
