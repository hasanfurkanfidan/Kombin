using Kombin.Api.Common.Http;
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

    // Also the answer for someone else's trip.
    public static readonly Error TripNotFound =
        Error.NotFound("Recommendation.TripNotFound", "The trip does not exist.");

    public static readonly Error TooManyTrips =
        Error.Conflict("Recommendation.TooManyTrips", $"At most {TripPlanning.MaxSavedTrips} trips can be saved; delete one first.");

    public static readonly Error TripOver =
        Error.Conflict("Recommendation.TripOver", "The trip is over; it cannot be planned again.");
}

/// <summary>The <c>date</c> parameter: <c>today</c>, <c>tomorrow</c> or <c>yyyy-MM-dd</c>, from today up to a week ahead (Europe/Istanbul).</summary>
internal static class OutfitDates
{
    public const int MaxDaysAhead = 7;

    /// <summary>What the validators say about a date, and the handlers when the day has turned between the validator's clock read and theirs.</summary>
    public static readonly string Message = $"'Date' must be today, tomorrow or a yyyy-MM-dd day within the next {MaxDaysAhead} days.";

    /// <summary>
    /// The 400 of a date the clock no longer accepts. The validator and the handler each read the clock, so a date valid at 23:59:59.95 in Istanbul
    /// can be yesterday a moment later: the handler answers what the validator would have, never a crash.
    /// </summary>
    public static IResult Rejected(HttpContext context) =>
        Problems.Validation(context, new Dictionary<string, string[]> { ["date"] = [Message] });

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
