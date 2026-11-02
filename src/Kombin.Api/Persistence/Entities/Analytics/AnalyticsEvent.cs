namespace Kombin.Api.Persistence.Entities.Analytics;

internal enum AnalyticsPlatform
{
    Ios,
    Android,
}

/// <summary>
/// One product-analytics event sent by the app. Append-only, apart from <see cref="UserId"/>, which is filled in when an anonymous
/// device signs in (<c>IAnalyticsIdentityLinker</c>). Carries no personal data: the names and properties come from an allow-list.
/// </summary>
internal sealed class AnalyticsEvent
{
    public long Id { get; set; }

    /// <summary>The signed-in user, or null while the device was anonymous (until the identity linker fills it in) and after the account is deleted.</summary>
    public Guid? UserId { get; set; }

    /// <summary>A stable random ID the app generates before login. Kept after login so the events of one install stay together.</summary>
    public string? AnonymousId { get; set; }

    /// <summary>One of the names in <c>AnalyticsEventCatalog</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A flat JSON object (at most 2 KB) whose keys are those the catalog allows for <see cref="Name"/>.</summary>
    public string Properties { get; set; } = "{}";

    public string SessionId { get; set; } = string.Empty;

    public AnalyticsPlatform Platform { get; set; }

    public string AppVersion { get; set; } = string.Empty;

    /// <summary>The client's clock, clamped to <see cref="ReceivedAt"/> when it is more than 7 days old or more than 5 minutes ahead.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}
