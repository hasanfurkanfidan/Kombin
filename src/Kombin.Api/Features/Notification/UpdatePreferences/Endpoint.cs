using System.Globalization;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Notification.UpdatePreferences;

/// <summary>
/// A partial update: a null field is left as it is. Quiet hours are <c>HH:mm</c> local times in Europe/Istanbul and come as a pair;
/// <c>ClearQuietHours: true</c> removes them.
/// </summary>
internal sealed record UpdatePreferencesRequest(
    bool? PriceAlerts,
    bool? StockAlerts,
    bool? Recommendations,
    int? MaxRecommendationsPerWeek,
    string? QuietHoursStart,
    string? QuietHoursEnd,
    bool? ClearQuietHours);

internal static class UpdatePreferencesEndpoint
{
    public const string TimeFormat = "HH:mm";

    public static void Map(RouteGroupBuilder group) =>
        group.MapPut("/", Handle)
            .WithName("UpdateNotificationPreferences")
            .WithValidation<UpdatePreferencesRequest>()
            .Produces<PreferencesResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    internal static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static async Task<IResult> Handle(UpdatePreferencesRequest request, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        if (request.ClearQuietHours != true && (request.QuietHoursStart is null) != (request.QuietHoursEnd is null))
        {
            return NotificationErrors.QuietHoursIncomplete.ToHttpResult();
        }

        var preference = await NotificationPreferences.GetOrCreateAsync(db, currentUser.UserId, cancellationToken);

        preference.PriceAlerts = request.PriceAlerts ?? preference.PriceAlerts;
        preference.StockAlerts = request.StockAlerts ?? preference.StockAlerts;
        preference.Recommendations = request.Recommendations ?? preference.Recommendations;
        preference.MaxRecommendationsPerWeek = request.MaxRecommendationsPerWeek ?? preference.MaxRecommendationsPerWeek;

        if (request.ClearQuietHours == true)
        {
            preference.QuietHoursStart = null;
            preference.QuietHoursEnd = null;
        }
        else if (TryParseTime(request.QuietHoursStart, out var start) && TryParseTime(request.QuietHoursEnd, out var end))
        {
            preference.QuietHoursStart = start;
            preference.QuietHoursEnd = end;
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(PreferencesResponse.From(preference));
    }
}
