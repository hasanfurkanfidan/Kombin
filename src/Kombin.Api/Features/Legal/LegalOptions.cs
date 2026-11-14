namespace Kombin.Api.Features.Legal;

/// <summary>
/// <c>Legal:*</c>: who the data controller is and which version of the legal texts is live. The pages print the placeholders
/// (<c>{{CONTROLLER_NAME}}</c>, <c>{{CONTACT_EMAIL}}</c>, <c>{{ADDRESS}}</c>) while a value is blank, and production refuses to start until all
/// three are set. Change <see cref="Version"/> whenever a text changes in a way users should know about.
/// </summary>
internal sealed class LegalOptions
{
    public const string SectionName = "Legal";

    public const string ControllerPlaceholder = "{{CONTROLLER_NAME}}";

    public const string EmailPlaceholder = "{{CONTACT_EMAIL}}";

    public const string AddressPlaceholder = "{{ADDRESS}}";

    /// <summary>The version of the privacy policy, terms and deletion page together; shown on each page and by <c>GET /api/v1/legal</c>.</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>The data controller (veri sorumlusu): a person or a company.</summary>
    public string ControllerName { get; set; } = string.Empty;

    /// <summary>Where users send privacy and deletion requests.</summary>
    public string ContactEmail { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// The public address of this API (<c>https://api.example.com</c>), used for the URLs in <c>GET /api/v1/legal</c>. Left blank, the address of
    /// the request is used, which is right behind forwarded headers.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public string ControllerNameOrPlaceholder => OrPlaceholder(ControllerName, ControllerPlaceholder);

    public string ContactEmailOrPlaceholder => OrPlaceholder(ContactEmail, EmailPlaceholder);

    public string AddressOrPlaceholder => OrPlaceholder(Address, AddressPlaceholder);

    private static string OrPlaceholder(string value, string placeholder) => string.IsNullOrWhiteSpace(value) ? placeholder : value.Trim();
}
