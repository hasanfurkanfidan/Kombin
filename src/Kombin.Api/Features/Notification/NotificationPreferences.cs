using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;
using Npgsql;

namespace Kombin.Api.Features.Notification;

/// <summary>What the preference endpoints return. Quiet hours are <c>HH:mm</c> local times in <see cref="TimeZone"/>.</summary>
internal sealed record PreferencesResponse(
    bool PriceAlerts,
    bool StockAlerts,
    bool Recommendations,
    int MaxRecommendationsPerWeek,
    string? QuietHoursStart,
    string? QuietHoursEnd,
    string TimeZone,
    DateTimeOffset UpdatedAt)
{
    public static PreferencesResponse From(NotificationPreference preference) => new(
        preference.PriceAlerts,
        preference.StockAlerts,
        preference.Recommendations,
        preference.MaxRecommendationsPerWeek,
        Format(preference.QuietHoursStart),
        Format(preference.QuietHoursEnd),
        QuietHours.ZoneId,
        preference.UpdatedAt);

    private static string? Format(TimeOnly? time) => time?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

internal static class NotificationPreferences
{
    /// <summary>
    /// The tracked preferences of the user, created with the defaults on first use. Call it before making other changes:
    /// creating the row saves the context.
    /// </summary>
    public static async Task<NotificationPreference> GetOrCreateAsync(KombinDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var preference = await db.NotificationPreferences.SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

        if (preference is not null)
        {
            return preference;
        }

        preference = new NotificationPreference { UserId = userId };
        db.NotificationPreferences.Add(preference);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return preference;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel request created it first; use theirs.
            db.Entry(preference).State = EntityState.Detached;
            return await db.NotificationPreferences.SingleAsync(candidate => candidate.UserId == userId, cancellationToken);
        }
    }
}
