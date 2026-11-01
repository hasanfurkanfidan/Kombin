using System.Text.Json;
using System.Text.RegularExpressions;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Catalog.ListProducts;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Notification;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Analytics;

/// <summary>A property an event may carry: its key, whether it must be there and what its value has to look like.</summary>
internal sealed record EventProperty(string Key, bool Required, Func<JsonElement, bool> IsValid, string Expected);

/// <summary>
/// The allow-list of analytics events and, per event, of its properties. Nothing outside it is stored, which is what keeps personal
/// data out: a property that is not listed (a search text, an email) is rejected, and values are identifiers, numbers or enum names,
/// never free text. Pure, so the validator and the tests share it.
/// </summary>
internal static partial class AnalyticsEventCatalog
{
    public const int MaxNameLength = 60;

    public const int MaxPropertiesBytes = 2048;

    /// <summary>Keys that are refused on every event, whatever the catalog says, because they name personal data.</summary>
    private static readonly HashSet<string> ForbiddenKeys = new(["email", "name", "phone", "address", "token"], StringComparer.Ordinal);

    private static readonly Dictionary<string, EventProperty[]> Events = new(StringComparer.Ordinal)
    {
        ["app_open"] = [],
        ["app_background"] = [],
        ["screen_view"] = [Required("screen", Identifier(60))],
        ["product_view"] = [Required("productId", Id), Optional("personaId", Id), Required("source", Identifier(40))],
        ["product_list_view"] = [Required("sort", OneOf<ProductSort>()), Required("filters", FilterList)],
        ["swipe"] = [Required("personaId", Id), Required("decision", OneOf<SwipeDecision>())],
        ["onboarding_step"] = [Required("step", Identifier(40)), Required("action", OneOf("next", "skip", "done"))],
        ["alert_created"] = [Required("kind", OneOf<AlertKind>())],
        ["buy_click"] = [Required("offerId", Id), Required("source", Identifier(40))],

        // The length of what was typed, never the text itself.
        ["search"] = [Required("queryLength", Integer(0, 200))],
        ["notification_open"] = [Required("notificationId", Integer(1, long.MaxValue)), Required("kind", OneOf<NotificationKind>())],
    };

    public static IEnumerable<string> Names => Events.Keys;

    public static bool IsKnown(string? name) => name is not null && Events.ContainsKey(name);

    /// <summary>
    /// What is wrong with <paramref name="properties"/> for the event <paramref name="name"/>, as (key, message) pairs; empty when it is fine.
    /// An unknown event has nothing to check against, so it yields nothing (its name is rejected on its own).
    /// </summary>
    public static IEnumerable<(string Key, string Message)> Check(string? name, IReadOnlyDictionary<string, JsonElement>? properties)
    {
        if (name is null || !Events.TryGetValue(name, out var allowed))
        {
            yield break;
        }

        var present = properties ?? new Dictionary<string, JsonElement>();

        foreach (var (key, value) in present)
        {
            if (IsForbiddenKey(key))
            {
                yield return (key, $"'{key}' is not allowed: events carry no personal data.");
            }
            else if (!allowed.Any(property => property.Key == key))
            {
                yield return (key, $"'{key}' is not a property of '{name}'.");
            }
            else if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                yield return (key, $"'{key}' must be a flat value, not an object or an array.");
            }
        }

        foreach (var property in allowed)
        {
            if (!present.TryGetValue(property.Key, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (property.Required)
                {
                    yield return (property.Key, $"'{property.Key}' is required for '{name}'.");
                }
            }
            else if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) && !property.IsValid(value))
            {
                yield return (property.Key, $"'{property.Key}' must be {property.Expected}.");
            }
        }
    }

    /// <summary>Case-insensitive, ignoring <c>_</c> and <c>-</c>, so <c>E-Mail</c> and <c>Phone</c> are caught too.</summary>
    public static bool IsForbiddenKey(string key) =>
        ForbiddenKeys.Contains(key.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant());

    /// <summary>The size the properties take as JSON, which is what the 2 KB limit counts.</summary>
    public static int SerializedSize(IReadOnlyDictionary<string, JsonElement>? properties) =>
        properties is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(properties).Length;

    /// <summary>The JSON object that is stored: the given properties without the null ones.</summary>
    public static string Serialize(IReadOnlyDictionary<string, JsonElement>? properties) =>
        JsonSerializer.Serialize(properties?.Where(pair => pair.Value.ValueKind != JsonValueKind.Null).ToDictionary(pair => pair.Key, pair => pair.Value) ?? []);

    private static EventProperty Required(string key, (Func<JsonElement, bool> IsValid, string Expected) rule) => new(key, true, rule.IsValid, rule.Expected);

    private static EventProperty Optional(string key, (Func<JsonElement, bool> IsValid, string Expected) rule) => new(key, false, rule.IsValid, rule.Expected);

    // Properties, not fields: the Events table above is built by a static initializer that runs before any field declared below it.
    private static (Func<JsonElement, bool>, string) Id =>
        (value => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty, "a GUID");

    private static (Func<JsonElement, bool>, string) FilterList =>
        (value => value.ValueKind == JsonValueKind.String && FilterListPattern().IsMatch(value.GetString()!), "a comma-separated list of filter names, at most 200 characters");

    /// <summary>A short code such as <c>product_detail</c>: letters, digits and <c>_ . : / -</c> only, so free text and addresses cannot get in.</summary>
    private static (Func<JsonElement, bool>, string) Identifier(int maxLength) =>
        (value => value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            && text.Length <= maxLength
            && IdentifierPattern().IsMatch(text),
        $"an identifier of 1-{maxLength} characters (letters, digits and _ . : / -)");

    private static (Func<JsonElement, bool>, string) Integer(long min, long max) =>
        (value => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= min && number <= max,
        max == long.MaxValue ? $"an integer of at least {min}" : $"an integer between {min} and {max}");

    private static (Func<JsonElement, bool>, string) OneOf<TEnum>()
        where TEnum : struct, Enum =>
        (value => value.ValueKind == JsonValueKind.String && EnumNames.IsValid<TEnum>(value.GetString()), $"one of: {string.Join(", ", Enum.GetNames<TEnum>())}");

    private static (Func<JsonElement, bool>, string) OneOf(params string[] values) =>
        (value => value.ValueKind == JsonValueKind.String && values.Contains(value.GetString(), StringComparer.OrdinalIgnoreCase), $"one of: {string.Join(", ", values)}");

    [GeneratedRegex(@"^[A-Za-z0-9_.:/\-]+$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.,:/\-]{0,200}$")]
    private static partial Regex FilterListPattern();
}
