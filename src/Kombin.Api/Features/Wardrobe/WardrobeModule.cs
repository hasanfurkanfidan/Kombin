using Hangfire;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Wardrobe.AddFromProduct;
using Kombin.Api.Features.Wardrobe.ArchiveItem;
using Kombin.Api.Features.Wardrobe.CreateUploadUrl;
using Kombin.Api.Features.Wardrobe.GetGaps;
using Kombin.Api.Features.Wardrobe.GetItem;
using Kombin.Api.Features.Wardrobe.GetStats;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Features.Wardrobe.ListItems;
using Kombin.Api.Features.Wardrobe.ProcessItem;
using Kombin.Api.Features.Wardrobe.RecordWear;
using Kombin.Api.Features.Wardrobe.UndoWear;
using Kombin.Api.Features.Wardrobe.UpdateItem;

namespace Kombin.Api.Features.Wardrobe;

internal static class WardrobeModule
{
    /// <summary>The named client that downloads catalog images for <c>from-product</c>; tests swap its handler.</summary>
    public const string CatalogImageClient = "wardrobe-catalog-images";

    public static IServiceCollection AddWardrobeFeature(this IServiceCollection services)
    {
        services.AddOptions<VisionOptions>().BindConfiguration(VisionOptions.SectionName);
        services.AddHttpClient<IVisionClient, VisionClient>(VisionClient.Configure);
        services.AddHttpClient(CatalogImageClient, client => client.Timeout = TimeSpan.FromSeconds(10));

        // Plain classes with constructor injection, like the other jobs: Hangfire activates them from a scope, the tests resolve them directly.
        services.AddScoped<WardrobeAnalyzeJob>();
        services.AddScoped<WardrobeCleanupJob>();

        return services;
    }

    public static IEndpointRouteBuilder MapWardrobeEndpoints(this IEndpointRouteBuilder app)
    {
        var wardrobe = app.MapGroup("/api/v1/me/wardrobe").RequireAuthorization().WithTags("Wardrobe");

        CreateUploadUrlEndpoint.Map(wardrobe);
        ProcessItemEndpoint.Map(wardrobe);
        AddFromProductEndpoint.Map(wardrobe);
        ListItemsEndpoint.Map(wardrobe);
        GetItemEndpoint.Map(wardrobe);
        UpdateItemEndpoint.Map(wardrobe);
        ArchiveItemEndpoint.Map(wardrobe);
        RecordWearEndpoint.Map(wardrobe);
        UndoWearEndpoint.Map(wardrobe);
        GetStatsEndpoint.Map(wardrobe);
        GetGapsEndpoint.Map(wardrobe);

        return app;
    }

    /// <summary>
    /// Registers the recurring <c>wardrobe-cleanup</c>. Call it after <c>UseAnalyticsJobs</c>; like the other registrations it does nothing
    /// unless <c>Hangfire:Enabled</c> is true. <c>wardrobe-analyze</c> is not recurring: <c>process</c> enqueues it per item.
    /// </summary>
    public static void UseWardrobeJobs(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(NotificationModule.HangfireEnabledKey))
        {
            return;
        }

        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<WardrobeCleanupJob>(
            WardrobeCleanupJob.JobId,
            job => job.RunAsync(CancellationToken.None),
            WardrobeCleanupJob.Cron,
            new RecurringJobOptions { TimeZone = QuietHours.Zone });
    }
}
