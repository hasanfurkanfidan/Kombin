using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Engagement;

internal static class EngagementErrors
{
    // Also the answer for an alert that belongs to someone else: whether it exists is none of the caller's business.
    public static readonly Error AlertNotFound =
        Error.NotFound("Engagement.AlertNotFound", "The alert does not exist.");

    public static readonly Error AlertExists =
        Error.Conflict("Engagement.AlertExists", "An active alert of this kind already exists for the product.");

    public static readonly Error TargetNotBelowCurrent =
        Error.Validation("Engagement.TargetNotBelowCurrent", "The target price must be below the product's current lowest price.");

    public static readonly Error OfferNotAvailable =
        Error.NotFound("Engagement.OfferNotAvailable", "The offer does not exist or is no longer available.");
}
