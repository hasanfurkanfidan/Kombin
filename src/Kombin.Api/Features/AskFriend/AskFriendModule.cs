using Kombin.Api.Common.Hosting;
using Kombin.Api.Features.AskFriend.CloseAsk;
using Kombin.Api.Features.AskFriend.CreateAsk;
using Kombin.Api.Features.AskFriend.GetAsk;
using Kombin.Api.Features.AskFriend.GetConsent;
using Kombin.Api.Features.AskFriend.HideAnswer;
using Kombin.Api.Features.AskFriend.ListAsks;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.AskFriend;

/// <summary>"Arkadaşına sor" (design: <c>docs/design/ask-a-friend.md</c>). PR AF2 brings the settings, the consent, the pure rules and the asker's API; the public page follows.</summary>
internal static class AskFriendModule
{
    public static IServiceCollection AddAskFriendFeature(this IServiceCollection services)
    {
        services.AddOptions<AskFriendOptions>().BindConfiguration(AskFriendOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AskFriendOptions>, AskFriendOptionsValidator>();
        services.AddScoped<AskImageCopier>();

        return services;
    }

    /// <summary>
    /// The routes are always mapped, so the OpenAPI document and the authorization matrix see them; with <c>AskFriend:Enabled</c> false they answer
    /// 404 <c>AskFriend.Disabled</c> before anything else runs. Creating and closing carry the <c>askfriend</c> rate-limit policy (30 an hour per
    /// user); the reads and hiding an answer stay under the global fallback.
    /// </summary>
    public static IEndpointRouteBuilder MapAskFriendEndpoints(this IEndpointRouteBuilder app)
    {
        var asks = app.MapGroup("/api/v1/me/asks")
            .RequireAuthorization()
            .WithTags("AskFriend")
            .AddEndpointFilter<RouteGroupBuilder, FeatureEnabledFilter<AskFriendOptions>>();

        GetAskConsentEndpoint.Map(asks);
        CreateAskEndpoint.Map(asks);
        ListAsksEndpoint.Map(asks);
        GetAskEndpoint.Map(asks);
        CloseAskEndpoint.Map(asks);
        HideAnswerEndpoint.Map(asks);

        return app;
    }
}
