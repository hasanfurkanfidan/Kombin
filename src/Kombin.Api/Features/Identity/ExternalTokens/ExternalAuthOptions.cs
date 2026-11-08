namespace Kombin.Api.Features.Identity.ExternalTokens;

/// <summary>
/// <c>Authentication:Google:ClientIds</c> and <c>Authentication:Apple:ClientIds</c> (env <c>Authentication__Google__ClientIds__0</c>, ...).
/// A provider with no client ID is switched off: its sign-ins answer <c>Identity.ExternalProviderDisabled</c>.
/// </summary>
internal sealed class ExternalAuthOptions
{
    public const string SectionName = "Authentication";

    /// <summary>The Android, iOS and web OAuth client IDs; an ID token's <c>aud</c> must be one of them.</summary>
    public ExternalProviderOptions Google { get; set; } = new();

    /// <summary>The iOS bundle ID (and later the Services ID for Android and web); an ID token's <c>aud</c> must be one of them.</summary>
    public ExternalProviderOptions Apple { get; set; } = new();
}

internal sealed class ExternalProviderOptions
{
    public string[] ClientIds { get; set; } = [];

    /// <summary>The client IDs without blanks, so an empty environment variable does not count as configured.</summary>
    public IReadOnlyList<string> ConfiguredClientIds() =>
        [.. ClientIds.Where(clientId => !string.IsNullOrWhiteSpace(clientId)).Select(clientId => clientId.Trim())];
}
