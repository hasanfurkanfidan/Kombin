namespace Kombin.Api.Persistence.Entities.Notification;

/// <summary>What a user wants to be pushed about. Not a <see cref="BaseEntity"/>: the user ID is the identity. Created lazily with the defaults.</summary>
internal sealed class NotificationPreference : IAuditable
{
    public const int DefaultMaxRecommendationsPerWeek = 3;

    public const int MaxRecommendationsPerWeekLimit = 7;

    public Guid UserId { get; set; }

    /// <summary>Price-drop and any-change alerts.</summary>
    public bool PriceAlerts { get; set; } = true;

    public bool StockAlerts { get; set; } = true;

    /// <summary>The weekly "new pieces that match you" digest.</summary>
    public bool Recommendations { get; set; } = true;

    public int MaxRecommendationsPerWeek { get; set; } = DefaultMaxRecommendationsPerWeek;

    /// <summary>Local time in Europe/Istanbul; both or neither. The interval may wrap past midnight.</summary>
    public TimeOnly? QuietHoursStart { get; set; }

    public TimeOnly? QuietHoursEnd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
