using Kombin.Api.Features.Recommendation.DismissOutfit;
using Kombin.Api.Features.Recommendation.GetDaily;
using Kombin.Api.Features.Recommendation.GetHowToWear;
using Kombin.Api.Features.Recommendation.GetOutfit;
using Kombin.Api.Features.Recommendation.Jobs;
using Kombin.Api.Features.Recommendation.ListSaved;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Features.Recommendation.RecordFeedback;
using Kombin.Api.Features.Recommendation.SaveOutfit;
using Kombin.Api.Features.Recommendation.WearOutfit;
using Kombin.Api.Features.Recommendation.Weather;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Recommendation;

internal static class RecommendationModule
{
    public static IServiceCollection AddRecommendationFeature(this IServiceCollection services)
    {
        // Weather per city and hour, outfit counts per wardrobe fingerprint.
        services.AddMemoryCache();

        services.AddOptions<WeatherOptions>().BindConfiguration(WeatherOptions.SectionName);
        services.AddHttpClient<IWeatherProvider, OpenMeteoWeatherProvider>(OpenMeteoWeatherProvider.Configure);

        services.AddOptions<NarrationOptions>().BindConfiguration(NarrationOptions.SectionName);
        services.AddOptions<AnthropicOptions>().BindConfiguration(AnthropicOptions.SectionName);
        services.AddSingleton<TemplateNarrator>();
        services.AddHttpClient<AnthropicNarrator>(AnthropicNarrator.Configure);

        // The narrator the background job uses; requests always answer with the template line (or a cached LLM one) first.
        services.AddScoped<IOutfitNarrator>(provider =>
            provider.GetRequiredService<IOptions<NarrationOptions>>().Value.Provider == Persistence.Entities.Recommendation.NarrationProvider.Anthropic
                ? provider.GetRequiredService<AnthropicNarrator>()
                : provider.GetRequiredService<TemplateNarrator>());

        services.AddScoped<OutfitStore>();
        services.AddScoped<OutfitNarrationJob>();

        return services;
    }

    public static IEndpointRouteBuilder MapRecommendationEndpoints(this IEndpointRouteBuilder app)
    {
        var outfits = app.MapGroup("/api/v1/me/outfits").RequireAuthorization().WithTags("Recommendation");

        GetDailyEndpoint.Map(outfits);
        GetHowToWearEndpoint.Map(outfits);
        ListSavedEndpoint.Map(outfits);
        GetOutfitEndpoint.Map(outfits);
        SaveOutfitEndpoint.Map(outfits);
        WearOutfitEndpoint.Map(outfits);
        RecordFeedbackEndpoint.Map(outfits);
        DismissOutfitEndpoint.Map(outfits);

        return app;
    }
}
