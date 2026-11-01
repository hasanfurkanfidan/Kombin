using System.Text.Json;
using System.Text.Json.Serialization;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification;

/// <summary>
/// The JSON object stored in <c>Notification.Data</c> and delivered to the app as the push payload; <c>deepLink</c> is what a tap opens.
/// Fields that do not apply to a kind are left out.
/// </summary>
internal static class NotificationData
{
    public const string SystemDeepLink = "kombin://notifications";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Payload(Guid? ProductId, Guid? OfferId, Guid? PersonaId, Guid? AlertId, IReadOnlyList<Guid>? ProductIds, string DeepLink);

    public static string ForAlert(Guid productId, Guid offerId, Guid? alertId) =>
        JsonSerializer.Serialize(new Payload(productId, offerId, null, alertId, null, $"kombin://product/{productId}"), Json);

    public static string ForNewMatches(Guid personaId, IReadOnlyList<Guid> productIds) =>
        JsonSerializer.Serialize(new Payload(null, null, personaId, null, productIds, $"kombin://matches/{personaId}"), Json);

    public static string ForSystem() =>
        JsonSerializer.Serialize(new Payload(null, null, null, null, null, SystemDeepLink), Json);
}

internal static class NotificationKinds
{
    public static NotificationKind From(AlertKind kind) => kind switch
    {
        AlertKind.PriceDrop => NotificationKind.PriceDrop,
        AlertKind.BackInStock => NotificationKind.BackInStock,
        _ => NotificationKind.AnyChange,
    };

    /// <summary>The preference that switches the kind off; <see cref="NotificationKind.System"/> has none.</summary>
    public static bool IsEnabled(NotificationPreference? preference, NotificationKind kind)
    {
        // A user who never touched the settings has no row and gets the defaults, which are all on.
        if (preference is null)
        {
            return true;
        }

        return kind switch
        {
            NotificationKind.PriceDrop or NotificationKind.AnyChange => preference.PriceAlerts,
            NotificationKind.BackInStock => preference.StockAlerts,
            NotificationKind.NewMatches => preference.Recommendations,
            _ => true,
        };
    }

    /// <summary>Collapses repeats: one price drop per product per 24 hours, whatever the alert that fired.</summary>
    public static string AlertDedupeKey(NotificationKind kind, Guid productId) => $"{kind.ToString().ToLowerInvariant()}:{productId}";
}
