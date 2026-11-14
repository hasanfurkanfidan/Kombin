using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Features.Engagement.CreateAlert;
using Kombin.Api.Features.Engagement.DeleteAlert;
using Kombin.Api.Features.Engagement.ListAlerts;
using Kombin.Api.Features.Engagement.ListClicks;
using Kombin.Api.Features.Engagement.ListSaved;
using Kombin.Api.Features.Engagement.ListSavedIds;
using Kombin.Api.Features.Engagement.ListTriggers;
using Kombin.Api.Features.Engagement.Redirect;
using Kombin.Api.Features.Engagement.SaveProduct;
using Kombin.Api.Features.Engagement.UnsaveProduct;
using Kombin.Api.Features.Engagement.UpdateAlert;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Engagement;

internal static class EngagementModule
{
    public static IServiceCollection AddEngagementFeature(this IServiceCollection services)
    {
        services.AddOptions<OutboundOptions>()
            .BindConfiguration(OutboundOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OutboundOptions>, OutboundOptionsValidator>();

        // Called by OfferUpserter, so alerts are evaluated as part of every catalog upsert.
        services.AddScoped<AlertEvaluator>();

        return services;
    }

    public static IEndpointRouteBuilder MapEngagementEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").RequireAuthorization();

        var saved = me.MapGroup("/saved").WithTags("Saved products");
        ListSavedEndpoint.Map(saved);
        ListSavedIdsEndpoint.Map(saved);
        SaveProductEndpoint.Map(saved);
        UnsaveProductEndpoint.Map(saved);

        var alerts = me.MapGroup("/alerts").WithTags("Alerts");
        CreateAlertEndpoint.Map(alerts);
        ListAlertsEndpoint.Map(alerts);
        ListTriggersEndpoint.Map(alerts);
        UpdateAlertEndpoint.Map(alerts);
        DeleteAlertEndpoint.Map(alerts);

        ListClicksEndpoint.Map(me.MapGroup("/clicks").WithTags("Clicks"));

        // Public on purpose: a buy click works signed out. The handler reads the user from the token when there is one.
        RedirectEndpoint.Map(app.MapGroup("/api/v1/go").WithTags("Outbound").RequireRateLimiting(RateLimitPolicies.Go));

        return app;
    }
}
