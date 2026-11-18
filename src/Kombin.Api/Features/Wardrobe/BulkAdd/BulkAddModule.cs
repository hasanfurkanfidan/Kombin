using Kombin.Api.Common.Hosting;
using Kombin.Api.Common.Results;
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

        return services;
    }

    /// <summary>
    /// Always mapped (the OpenAPI document and the authorization matrix see the routes); with <c>BulkAdd:Enabled</c> false every one answers 404
    /// <c>Wardrobe.BulkDisabled</c> before anything else runs. <paramref name="wardrobe"/> is the <c>/api/v1/me/wardrobe</c> group, already
    /// authorized. Task BA3 (PR 2) maps upload-url, process and the status; confirm and cancel still answer
    /// <see cref="BulkAddErrors.NotImplementedYet"/> when the flag is on until PR 3 replaces them, one <c>Endpoint.cs</c> each.
    /// </summary>
    public static RouteGroupBuilder MapBulkAddEndpoints(this RouteGroupBuilder wardrobe)
    {
        ArgumentNullException.ThrowIfNull(wardrobe);

        var bulk = wardrobe.MapGroup("/bulk").AddEndpointFilter<RouteGroupBuilder, FeatureEnabledFilter<BulkAddOptions>>();

        BulkCreateUploadUrlEndpoint.Map(bulk);
        BulkProcessEndpoint.Map(bulk);
        BulkGetEndpoint.Map(bulk);

        // PR 3: confirm, cancel.
        bulk.MapPost("/{id:guid}/confirm", (Guid id) => NotImplementedYet());
        bulk.MapDelete("/{id:guid}", (Guid id) => NotImplementedYet());

        return wardrobe;
    }

    private static IResult NotImplementedYet() => BulkAddErrors.NotImplementedYet.ToHttpResult();
}
