namespace Kombin.Api.Features.Engagement;

/// <summary>The screens and placements a buy click can come from; anything else is recorded as <see cref="Unknown"/>.</summary>
internal static class ClickSources
{
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> Allowed = ["product_detail", "outfit", "saved", "alert", "feed", Unknown];

    public static string Normalize(string? source)
    {
        var trimmed = source?.Trim();

        return Allowed.FirstOrDefault(allowed => string.Equals(allowed, trimmed, StringComparison.OrdinalIgnoreCase)) ?? Unknown;
    }
}
