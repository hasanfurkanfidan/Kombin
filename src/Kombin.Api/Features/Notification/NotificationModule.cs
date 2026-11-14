using Hangfire;
using Hangfire.PostgreSql;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Hosting;
using Kombin.Api.Features.Notification.DeleteDevice;
using Kombin.Api.Features.Notification.GetPreferences;
using Kombin.Api.Features.Notification.GetUnreadCount;
using Kombin.Api.Features.Notification.Jobs;
using Kombin.Api.Features.Notification.ListNotifications;
using Kombin.Api.Features.Notification.MarkAllRead;
using Kombin.Api.Features.Notification.MarkRead;
using Kombin.Api.Features.Notification.RegisterDevice;
using Kombin.Api.Features.Notification.SendTest;
using Kombin.Api.Features.Notification.UpdatePreferences;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Notification;

internal static class NotificationModule
{
    /// <summary>Whether this process runs the Hangfire server, registers the recurring jobs and (in Development) serves the dashboard.</summary>
    public const string HangfireEnabledKey = "Hangfire:Enabled";

    public const string HangfireSchema = "hangfire";

    public const string DashboardPath = "/hangfire";

    public static IServiceCollection AddNotificationFeature(this IServiceCollection services)
    {
        services.AddOptions<ExpoOptions>().BindConfiguration(ExpoOptions.SectionName);
        services.AddHttpClient<IExpoPushClient, ExpoPushClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IDeviceDeactivator, DeviceDeactivator>();

        // Plain classes with constructor injection; Hangfire activates them from a scope per job, the tests resolve them directly.
        services.AddScoped<AlertDispatcherJob>();
        services.AddScoped<PushSenderJob>();
        services.AddScoped<PushReceiptsJob>();
        services.AddScoped<WeeklyMatchesJob>();

        // Registering is lazy: the storage (and its schema) is only created when something first asks for it, which
        // UseNotificationJobs does after the EF migrations ran.
        services.AddHangfire((provider, configuration) => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(
                    provider.GetRequiredService<IConfiguration>().GetConnectionString(PersistenceExtensions.ConnectionStringName)
                    ?? throw new InvalidOperationException($"Connection string 'ConnectionStrings:{PersistenceExtensions.ConnectionStringName}' is not configured.")),
                // Long polling (LISTEN/NOTIFY) starts an enqueued job at once instead of on the next 15-second queue poll:
                // wardrobe-analyze is on the "photo to ready in ~3 seconds" path.
                new PostgreSqlStorageOptions { SchemaName = HangfireSchema, EnableLongPolling = true }));

        AddHangfireServerWhenEnabled(services);

        return services;
    }

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").RequireAuthorization();

        var devices = me.MapGroup("/devices").WithTags("Devices");
        RegisterDeviceEndpoint.Map(devices);
        DeleteDeviceEndpoint.Map(devices);

        var preferences = me.MapGroup("/notification-preferences").WithTags("Notifications");
        GetPreferencesEndpoint.Map(preferences);
        UpdatePreferencesEndpoint.Map(preferences);

        var notifications = me.MapGroup("/notifications").WithTags("Notifications");
        ListNotificationsEndpoint.Map(notifications);
        GetUnreadCountEndpoint.Map(notifications);
        MarkReadEndpoint.Map(notifications);
        MarkAllReadEndpoint.Map(notifications);

        // Internal: for QA on real devices, with X-Internal-Key.
        var internalNotifications = app.MapGroup("/api/v1/internal/notifications").WithTags("Notifications (internal)").RequireInternalKey();
        SendTestEndpoint.Map(internalNotifications);

        return app;
    }

    /// <summary>
    /// Registers the recurring jobs and, in Development (or with <c>Diagnostics:Expose</c>), the dashboard. Call it after the database migrations: it is what first touches
    /// the Hangfire storage and creates its <c>hangfire</c> schema. Does nothing unless <c>Hangfire:Enabled</c> is true, so tests and
    /// one-off tooling never touch the job tables. The dashboard is not mapped at all outside Development unless <c>Diagnostics:Expose</c> is true.
    /// </summary>
    public static void UseNotificationJobs(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(HangfireEnabledKey))
        {
            return;
        }

        // Development, or an environment that set Diagnostics:Expose on purpose (then only behind the founder's /admin sign-in).
        if (app.ExposesDiagnostics())
        {
            app.UseHangfireDashboard(DashboardPath, new DashboardOptions
            {
                DashboardTitle = "Kombin jobs",
                Authorization = [],
                AsyncAuthorization = [new HostingExtensions.DashboardAuthorization(open: app.Environment.IsDevelopment())],
            });
        }

        var recurring = app.Services.GetRequiredService<IRecurringJobManager>();

        recurring.AddOrUpdate<AlertDispatcherJob>(AlertDispatcherJob.JobId, job => job.RunAsync(CancellationToken.None), Cron.Minutely());
        recurring.AddOrUpdate<PushSenderJob>(PushSenderJob.JobId, job => job.RunAsync(CancellationToken.None), Cron.Minutely());
        recurring.AddOrUpdate<PushReceiptsJob>(PushReceiptsJob.JobId, job => job.RunAsync(CancellationToken.None), "*/15 * * * *");
        recurring.AddOrUpdate<WeeklyMatchesJob>(
            WeeklyMatchesJob.JobId,
            job => job.RunAsync(CancellationToken.None),
            WeeklyMatchesJob.Cron,
            new RecurringJobOptions { TimeZone = QuietHours.Zone });
    }

    /// <summary>
    /// <c>AddHangfireServer</c> would start the server unconditionally, but whether it is enabled can only be read once the host is
    /// built (the integration tests set it after Program.cs ran). So its hosted service is wrapped and created lazily at start-up.
    /// </summary>
    private static void AddHangfireServerWhenEnabled(IServiceCollection services)
    {
        var before = services.Count;
        services.AddHangfireServer(options => options.WorkerCount = 4);

        for (var index = before; index < services.Count; index++)
        {
            var descriptor = services[index];

            if (descriptor.ServiceType != typeof(IHostedService) || descriptor.IsKeyedService)
            {
                continue;
            }

            services[index] = ServiceDescriptor.Singleton<IHostedService>(provider => new EnabledOnlyHostedService(
                provider.GetRequiredService<IConfiguration>(),
                () => (IHostedService)(descriptor.ImplementationInstance
                    ?? descriptor.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!))));
        }
    }

    private sealed class EnabledOnlyHostedService(IConfiguration configuration, Func<IHostedService> create) : IHostedService, IDisposable
    {
        private IHostedService? _inner;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!configuration.GetValue<bool>(HangfireEnabledKey))
            {
                return;
            }

            _inner = create();
            await _inner.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;

        public void Dispose() => (_inner as IDisposable)?.Dispose();
    }

}
