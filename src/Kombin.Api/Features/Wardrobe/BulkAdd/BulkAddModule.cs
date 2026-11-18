using Kombin.Api.Common.Results;
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

        return services;
    }

    /// <summary>
    /// Always mapped (the OpenAPI document and the authorization matrix see the routes); with <c>BulkAdd:Enabled</c> false every one answers 404
    /// <c>Wardrobe.BulkDisabled</c> before anything else runs. <paramref name="wardrobe"/> is the <c>/api/v1/me/wardrobe</c> group, already
    /// authorized. Task BA2 PR 1 maps the five routes with no behaviour (they answer <see cref="BulkAddErrors.NotImplementedYet"/> when the flag is
    /// on); PR 2 and PR 3 replace them with the use cases of the design note, one <c>Endpoint.cs</c> each.
    /// </summary>
    public static RouteGroupBuilder MapBulkAddEndpoints(this RouteGroupBuilder wardrobe)
    {
        ArgumentNullException.ThrowIfNull(wardrobe);

        var bulk = wardrobe.MapGroup("/bulk").AddEndpointFilter<RouteGroupBuilder, BulkAddEnabledFilter>();

        // PR 2: upload-url, process, status; PR 3: confirm, cancel.
        bulk.MapPost("/upload-url", NotImplementedYet);
        bulk.MapPost("/{id:guid}/process", (Guid id) => NotImplementedYet());
        bulk.MapGet("/{id:guid}", (Guid id) => NotImplementedYet());
        bulk.MapPost("/{id:guid}/confirm", (Guid id) => NotImplementedYet());
        bulk.MapDelete("/{id:guid}", (Guid id) => NotImplementedYet());

        return wardrobe;
    }

    private static IResult NotImplementedYet() => BulkAddErrors.NotImplementedYet.ToHttpResult();
}

/// <summary>
/// Answers 404 <c>Wardrobe.BulkDisabled</c> while <c>BulkAdd:Enabled</c> is false (the default).
/// TODO(question 7 of docs/design/bulk-add.md): this is the third copy of League's and TryOn's filters. Extract <c>FeatureEnabledFilter</c> to
/// <c>Common/Hosting</c> and make the three use it, once task B9 (which has the TryOn module open) is committed.
/// </summary>
internal sealed class BulkAddEnabledFilter(IOptionsMonitor<BulkAddOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        options.CurrentValue.Enabled ? await next(context) : BulkAddErrors.Disabled.ToHttpResult();
}
