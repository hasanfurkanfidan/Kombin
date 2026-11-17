using System.Text.Json;
using System.Text.RegularExpressions;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Catalog.ListProducts;
using Kombin.Api.Features.Lens;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Notification;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;

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

        // Wardrobe: `status` is how an add ended (the analysis did not finish in time is `Timeout`); the duration is capped at an hour.
        ["wardrobe_add_start"] = [Required("source", OneOf("camera", "library", "catalog"))],
        ["wardrobe_add_done"] = [Required("status", OneOf("Ready", "Failed", "Timeout")), Required("durationMs", Integer(0, 3_600_000))],

        // Outfits: `status` is the outfit's completeness (Complete, NeedsOne, CatalogOnly), `occasion` the request's occasion name.
        ["outfit_view"] = [Required("kind", OneOf<OutfitKind>()), Required("status", OneOf<OutfitCompleteness>()), Optional("occasion", OneOf<Occasion>())],
        ["outfit_shuffle"] = [],
        ["outfit_feedback"] = [Required("action", OneOf<OutfitFeedbackAction>()), Optional("reason", OneOf<DismissReason>())],
        ["outfit_gap_click"] = [],

        // Budget outfits: the budget as a band, never the amount; `result` says whether outfits fit, fit only without shoes, only the closest
        // one over the budget came back, or nothing did. Shuffling is the next seed.
        ["budget_outfit_view"] =
        [
            Required("budgetBand", OneOf("under500", "500to1500", "1500to5000", "over5000")),
            Required("outfitCount", Integer(0, 5)),
            Required("result", OneOf("fits", "withoutShoes", "overBudget", "none")),
            Optional("occasion", OneOf<Occasion>()),
        ],
        ["budget_outfit_shuffle"] = [Required("seed", Integer(0, 1000))],

        // Trips: numbers only (never the destination or the dates). `source` says whether a plan was just made or a saved one opened; a gap
        // click names the slot of the suggested piece.
        ["trip_plan_create"] =
        [
            Required("days", Integer(1, 14)),
            Required("pieceCount", Integer(0, 60)),
            Required("coveragePercent", Integer(0, 100)),
            Required("gapCount", Integer(0, 20)),
        ],
        ["trip_plan_view"] = [Required("days", Integer(1, 14)), Required("source", OneOf("plan", "saved"))],
        ["trip_gap_click"] = [Required("slot", OneOf<OutfitSlot>())],

        // Pairings: how many pieces the wardrobe item goes with (capped like the engine's counts) and whether a catalog suggestion is shown.
        ["wardrobe_pairings_view"] = [Required("companionTotal", Integer(0, 500)), Required("hasUnlock", Boolean)],
        ["wardrobe_unlock_click"] = [],

        // Wardrobe summary ("Dolap değeri", "Giymediklerin"): counts only, never a price. `hasEstimate` is whether the total shown includes estimated prices.
        ["wardrobe_summary_view"] = [Required("pieceCount", Integer(0, 2000)), Required("unwornCount", Integer(0, 2000)), Required("hasEstimate", Boolean)],

        // Size memory ("Aldın mı? Beden uydu mu?"): the question by ID, the answer by enum name; the size itself is never an event property.
        ["size_prompt_view"] = [Required("promptId", Id)],
        ["size_prompt_answer"] = [Required("promptId", Id), Required("answer", OneOf<SizePromptAnswer>())],

        // Social: posts are referred to by ID only (never the caption). `position` is the card's index in the feed; `status` how a post
        // flow ended (`Cancelled` when the user backed out); a tag tap says what kind of tag it was and whether it had a price.
        ["post_view"] = [Required("postId", Id), Required("source", OneOf("feed", "profile", "creator", "mine", "link")), Optional("position", Integer(0, 500))],
        ["post_tag_tap"] = [Required("postId", Id), Required("tagKind", OneOf<PostTagKind>()), Required("hasOffer", Boolean)],
        ["post_like"] = [Required("postId", Id), Required("liked", Boolean)],
        ["post_create_start"] = [Required("kind", OneOf<PostKind>()), Required("source", OneOf("camera", "library", "outfit"))],
        ["post_create_done"] =
        [
            Required("kind", OneOf<PostKind>()),
            Required("status", OneOf("Live", "Draft", "NeedsReview", "Blocked", "Failed", "Cancelled")),
            Required("tagCount", Integer(0, 6)),
            Optional("visibility", OneOf<PostVisibility>()),
            Required("durationMs", Integer(0, 3_600_000)),
        ],
        ["post_report"] = [Required("postId", Id), Required("reason", OneOf<ReportReason>())],
        ["creator_profile_view"] = [Required("creatorId", Id)],
        ["try_with_wardrobe"] = [Required("postId", Id), Required("anchorCount", Integer(0, 3)), Required("outfitCount", Integer(0, 10))],

        // Identity: a Google or Apple sign-in succeeded; `isNewUser` is true when it created the account.
        ["auth_external"] = [Required("provider", OneOf("google", "apple")), Required("isNewUser", Boolean)],

        // Visual search ("Görselden ürün bul"): never image data or text. `entry` is where the screen was opened; a search reports how many
        // regions and results it got, how long it took on the device and where the photo came from; a result tap its position in the region's
        // rail and the response's `level` (`close` / `similar`); adding a region to the wardrobe its category family (absent for the whole photo).
        ["lens_open"] = [Required("entry", OneOf("explore", "home", "other"))],
        ["lens_search"] =
        [
            Required("regionCount", Integer(0, 6)),
            Required("resultCount", Integer(0, 72)),
            Required("durationMs", Integer(0, 600_000)),
            Required("source", OneOf("camera", "gallery")),
        ],
        ["lens_result_tap"] = [Required("productId", Id), Required("position", Integer(0, 11)), Required("similarity", OneOf("close", "similar"))],
        ["lens_add_to_wardrobe"] = [Optional("family", OneOf<CategoryFamily>())],

        // Kombin Ligi (first-month measurement): never a pair, entry, image or player identifier. A vote says which side the player chose (or that
        // they skipped) and whether the server counted it (`counted` comes from the vote's response); the results screen says which state it showed (the league's `LeagueResultStatus` names, spelled out here: a feature does not name another one's types; `AnalyticsValidatorTests` pins them).
        ["league_join"] = [],
        ["league_leave"] = [],
        ["league_vote"] = [Required("choice", OneOf("left", "right", "skip")), Required("counted", Boolean)],
        ["league_report"] = [],
        ["league_results_view"] = [Required("status", OneOf("Final", "UnderReview", "Removed"))],
        ["league_table_view"] = [],
    };

    public static IEnumerable<string> Names => Events.Keys;

    public static bool IsKnown(string? name) => name is not null && Events.ContainsKey(name);

    /// <summary>
    /// Whether the event can be stored: null when it can, otherwise <see cref="RejectionReasons.UnknownEvent"/> or
    /// <see cref="RejectionReasons.InvalidProperties"/>. Judges the event alone, so one bad event never costs the rest of its batch.
    /// </summary>
    public static string? Reject(string? name, IReadOnlyDictionary<string, JsonElement>? properties)
    {
        if (!IsKnown(name))
        {
            return RejectionReasons.UnknownEvent;
        }

        return SerializedSize(properties) > MaxPropertiesBytes || Check(name, properties).Any()
            ? RejectionReasons.InvalidProperties
            : null;
    }

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

    private static (Func<JsonElement, bool>, string) Boolean =>
        (value => value.ValueKind is JsonValueKind.True or JsonValueKind.False, "true or false");

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
