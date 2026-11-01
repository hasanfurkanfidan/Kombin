using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Analytics;

internal static class AnalyticsErrors
{
    // Raised by the request validator (an event name outside the allow-list fails the whole batch); the code is what clients read.
    public static readonly Error UnknownEvent =
        Error.Validation("Analytics.UnknownEvent", "The event name is not in the allow-list.");
}
