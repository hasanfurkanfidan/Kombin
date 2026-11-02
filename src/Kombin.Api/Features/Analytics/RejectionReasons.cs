namespace Kombin.Api.Features.Analytics;

/// <summary>
/// Why <c>POST /events</c> did not store an event. Each is per event, reported with the event's index in <c>rejected</c> of the 202;
/// the rest of the batch is stored. Clients read these strings.
/// </summary>
internal static class RejectionReasons
{
    /// <summary>The name is not in the allow-list.</summary>
    public const string UnknownEvent = "unknown_event";

    /// <summary>A property is not listed for the event, has the wrong value or type, is missing, names personal data, or the properties exceed 2 KB.</summary>
    public const string InvalidProperties = "invalid_properties";

    /// <summary>An anonymous caller sent the event without an <c>anonymousId</c>, so it could never be attributed.</summary>
    public const string MissingIdentity = "missing_identity";

    /// <summary>The event repeats one earlier in the same batch with the same session, name and time.</summary>
    public const string Duplicate = "duplicate";
}
