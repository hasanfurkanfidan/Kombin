using System.Text.RegularExpressions;

namespace Kombin.Api.Features.Engagement;

/// <summary>
/// Builds the URL a buy click is redirected to. With a seller template the placeholders <c>{url}</c> (URL-encoded offer URL),
/// <c>{rawUrl}</c>, <c>{clickId}</c>, <c>{source}</c> and <c>{p.key}</c> are substituted; without one the offer URL gets
/// <c>utm_*</c> and <c>kombin_click</c> parameters, merged into its existing query string.
/// </summary>
internal static partial class AffiliateUrlBuilder
{
    public const string ClickParameter = "kombin_click";

    private static readonly string[] OwnParameters = ["utm_source", "utm_medium", "utm_campaign", ClickParameter];

    /// <summary>A template that expands to something other than an absolute http(s) URL is a configuration mistake; the default applies instead.</summary>
    public static string Build(
        string offerUrl,
        string? template,
        IReadOnlyDictionary<string, string>? parameters,
        long clickId,
        string source)
    {
        if (!string.IsNullOrWhiteSpace(template))
        {
            var expanded = Expand(template, offerUrl, parameters, clickId, source);

            if (Uri.TryCreate(expanded, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                return expanded;
            }
        }

        return AppendTracking(offerUrl, clickId, source);
    }

    private static string Expand(string template, string offerUrl, IReadOnlyDictionary<string, string>? parameters, long clickId, string source) =>
        Placeholder().Replace(template, match => match.Groups["name"].Value switch
        {
            "url" => Uri.EscapeDataString(offerUrl),
            "rawUrl" => offerUrl,
            "clickId" => clickId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "source" => Uri.EscapeDataString(source),
            var name when name.StartsWith("p.", StringComparison.Ordinal) =>
                parameters is not null && parameters.TryGetValue(name[2..], out var value) ? Uri.EscapeDataString(value) : string.Empty,
            _ => match.Value,
        });

    /// <summary>Keeps the offer's own query and fragment byte for byte, drops earlier values of our parameters and appends ours.</summary>
    private static string AppendTracking(string offerUrl, long clickId, string source)
    {
        var fragmentStart = offerUrl.IndexOf('#', StringComparison.Ordinal);
        var fragment = fragmentStart < 0 ? string.Empty : offerUrl[fragmentStart..];
        var withoutFragment = fragmentStart < 0 ? offerUrl : offerUrl[..fragmentStart];

        var queryStart = withoutFragment.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? withoutFragment : withoutFragment[..queryStart];
        var existing = queryStart < 0
            ? []
            : withoutFragment[(queryStart + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair => !OwnParameters.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0]), StringComparer.Ordinal));

        string[] added =
        [
            "utm_source=kombin",
            "utm_medium=app",
            $"utm_campaign={Uri.EscapeDataString(source)}",
            $"{ClickParameter}={clickId}",
        ];

        return $"{path}?{string.Join('&', existing.Concat(added))}{fragment}";
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9_.\-]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
