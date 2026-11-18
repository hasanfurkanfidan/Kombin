using Kombin.Api.Common.Results;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.Hosting;

/// <summary>
/// The options of a feature that ships switched off (<c>League:Enabled</c>, <c>TryOn:Enabled</c>, <c>BulkAdd:Enabled</c>): the flag and the 404 every
/// route of the feature answers while it is false (the routes stay mapped, so the OpenAPI document and the authorization matrix see them).
/// </summary>
internal interface IFeatureOptions
{
    bool Enabled { get; }

    /// <summary>The problem the routes answer while <see cref="Enabled"/> is false, e.g. <c>League.Disabled</c>.</summary>
    static abstract Error Disabled { get; }
}

/// <summary>
/// Answers the feature's <see cref="IFeatureOptions.Disabled"/> before anything else runs while its flag is false; the flag is read on every request
/// (<see cref="IOptionsMonitor{TOptions}"/>), so a configuration reload switches the feature. Use it as
/// <c>.AddEndpointFilter&lt;RouteGroupBuilder, FeatureEnabledFilter&lt;TheOptions&gt;&gt;()</c> on the feature's group.
/// </summary>
internal sealed class FeatureEnabledFilter<TOptions>(IOptionsMonitor<TOptions> options) : IEndpointFilter
    where TOptions : class, IFeatureOptions
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        options.CurrentValue.Enabled ? await next(context) : TOptions.Disabled.ToHttpResult();
}
