using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Profile;

internal static class StyleCardFeed
{
    /// <summary>The audiences of cards a profile with this audience gets: a Unisex profile sees everything, the others their own plus Unisex.</summary>
    public static bool IsVisibleTo(Audience profileAudience, Audience cardAudience) =>
        profileAudience == Audience.Unisex || cardAudience == profileAudience || cardAudience == Audience.Unisex;
}
