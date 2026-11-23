using Hangfire;
using Kombin.Api.Common.Hosting;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Wardrobe.BulkCancel;
using Kombin.Api.Features.Wardrobe.BulkConfirm;
using Kombin.Api.Features.Wardrobe.BulkCreateUploadUrl;
using Kombin.Api.Features.Wardrobe.BulkGet;
using Kombin.Api.Features.Wardrobe.BulkProcess;
using Kombin.Api.Features.Wardrobe.Jobs;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Wardrobe.BulkAdd;

/// <summary>
/// "Toplu ekle" (design: <c>docs/design/bulk-add.md</c>): one photo of several garments → a review screen → ordinary wardrobe items. Part of the
/// Wardrobe feature (<see cref="WardrobeModule"/> calls this), not a feature of its own. Off by default (<c>BulkAdd:Enabled</c>).
/// </summary>
internal static class BulkAddModule
{
    public static IServiceCollection AddBulkAddFeature(this IServiceCollection services)
    {
        services.AddOptions<BulkAddOptions>().BindConfiguration(BulkAddOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BulkAddOptions>, BulkAddOptionsValidator>();

        services.AddScoped<BulkAddAnalyzeJob>();
        services.AddScoped<BulkAddModerateJob>();
        services.AddScoped<BulkAddCleanupJob>();

        return services;
    }

    /// <summary>
    /// Always mapped (the OpenAPI document and the authorization matrix see the routes); with <c>BulkAdd:Enabled</c> false every one answers 404
    /// <c>Wardrobe.BulkDisabled</c> before anything else runs. <paramref name="wardrobe"/> is the <c>/api/v1/me/wardrobe</c> group, already
    /// authorized. One <c>Endpoint.cs</c> per route.
    /// </summary>
    public static RouteGroupBuilder MapBulkAddEndpoints(this RouteGroupBuilder wardrobe)
    {
        ArgumentNullException.ThrowIfNull(wardrobe);

        var bulk = wardrobe.MapGroup("/bulk").AddEndpointFilter<RouteGroupBuilder, FeatureEnabledFilter<BulkAddOptions>>();

        BulkCreateUploadUrlEndpoint.Map(bulk);
        BulkProcessEndpoint.Map(bulk);
        BulkGetEndpoint.Map(bulk);
        BulkConfirmEndpoint.Map(bulk);
        BulkCancelEndpoint.Map(bulk);

        return wardrobe;
    }

    /// <summary>
    /// Registers the recurring <c>bulk-add-cleanup</c> (called by <c>UseWardrobeJobs</c>, so only with <c>Hangfire:Enabled</c>). Deliberately not
    /// behind <c>BulkAdd:Enabled</c>: staging must still disappear when the flag is switched off. <c>bulk-add-analyze</c> and <c>bulk-add-moderate</c>
    /// are fire-and-forget, enqueued by <c>process</c> and <c>confirm</c>.
    /// </summary>
    public static void UseBulkAddJobs(this IRecurringJobManager recurring)
    {
        ArgumentNullException.ThrowIfNull(recurring);

        recurring.AddOrUpdate<BulkAddCleanupJob>(
            BulkAddCleanupJob.JobId,
            job => job.RunAsync(CancellationToken.None),
            BulkAddCleanupJob.Cron,
            new RecurringJobOptions { TimeZone = Istanbul.Zone });
    }
}
