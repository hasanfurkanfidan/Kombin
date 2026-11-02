using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Recommendation;

internal static class RecommendationErrors
{
    // Also the answer for someone else's outfit: whether it exists is none of the caller's business.
    public static readonly Error OutfitNotFound =
        Error.NotFound("Recommendation.OutfitNotFound", "The outfit does not exist.");

    public static readonly Error OutfitDismissed =
        Error.Conflict("Recommendation.OutfitDismissed", "The outfit has been dismissed.");

    public static readonly Error ProductNotFound =
        Error.NotFound("Recommendation.ProductNotFound", "The product does not exist.");

    /// <summary>Also for an archived item, an item still being analysed, and one without a category: none can anchor an outfit.</summary>
    public static readonly Error WardrobeItemNotFound =
        Error.NotFound("Recommendation.WardrobeItemNotFound", "The wardrobe item does not exist or cannot be worn yet.");

    public static readonly Error NothingToWear =
        Error.Conflict("Recommendation.NothingToWear", "None of the outfit's pieces is in the wardrobe.");
}

/// <summary>The <c>date</c> parameter: <c>today</c>, <c>tomorrow</c> or <c>yyyy-MM-dd</c>, from today up to a week ahead (Europe/Istanbul).</summary>
internal static class OutfitDates
{
    public const int MaxDaysAhead = 7;

    public static DateOnly? Resolve(string? value, DateOnly today)
    {
        var trimmed = value?.Trim();

        DateOnly? day = trimmed switch
        {
            null or "" => today,
            _ when trimmed.Equals("today", StringComparison.OrdinalIgnoreCase) => today,
            _ when trimmed.Equals("tomorrow", StringComparison.OrdinalIgnoreCase) => today.AddDays(1),
            _ => DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed
                : null,
        };

        return day is { } resolved && resolved >= today && resolved <= today.AddDays(MaxDaysAhead) ? resolved : null;
    }
}
