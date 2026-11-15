using Kombin.Api.Features.Recommendation.DeleteTrip;
using Kombin.Api.Features.Recommendation.DismissOutfit;
using Kombin.Api.Features.Recommendation.GetDaily;
using Kombin.Api.Features.Recommendation.GetHowToWear;
using Kombin.Api.Features.Recommendation.GetOutfit;
using Kombin.Api.Features.Recommendation.GetTrip;
using Kombin.Api.Features.Recommendation.Jobs;
using Kombin.Api.Features.Recommendation.ListSaved;
using Kombin.Api.Features.Recommendation.ListTrips;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Features.Recommendation.PlanTrip;
using Kombin.Api.Features.Recommendation.RecordFeedback;
using Kombin.Api.Features.Recommendation.ReplanTrip;
using Kombin.Api.Features.Recommendation.SaveOutfit;
using Kombin.Api.Features.Recommendation.SaveTrip;
using Kombin.Api.Features.Recommendation.SuggestBudget;
using Kombin.Api.Features.Recommendation.SuggestBudgetAnonymous;
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
        SuggestBudgetEndpoint.Map(outfits);
        GetHowToWearEndpoint.Map(outfits);
        ListSavedEndpoint.Map(outfits);
        GetOutfitEndpoint.Map(outfits);
        SaveOutfitEndpoint.Map(outfits);
        WearOutfitEndpoint.Map(outfits);
        RecordFeedbackEndpoint.Map(outfits);
        DismissOutfitEndpoint.Map(outfits);

        // "Valiz hazırlama": plan, save, list, read, delete, re-plan.
        var trips = app.MapGroup("/api/v1/me/trips").RequireAuthorization().WithTags("Recommendation");
        PlanTripEndpoint.Map(trips);
        SaveTripEndpoint.Map(trips);
        ListTripsEndpoint.Map(trips);
        GetTripEndpoint.Map(trips);
        DeleteTripEndpoint.Map(trips);
        ReplanTripEndpoint.Map(trips);

        // Public: "bütçeye göre kombin" for visitors (acquisition); rate limited like visual search.
        var anonymous = app.MapGroup("/api/v1/outfits").WithTags("Recommendation");
        SuggestBudgetAnonymousEndpoint.Map(anonymous);

        return app;
    }
}
