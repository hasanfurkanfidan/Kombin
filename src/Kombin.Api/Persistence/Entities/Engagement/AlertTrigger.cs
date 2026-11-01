namespace Kombin.Api.Persistence.Entities.Engagement;

/// <summary>
/// Append-only record that an alert fired; it doubles as the user's notification feed. <see cref="PriceAlertId"/> becomes
/// null when the alert is deleted, the trigger itself stays.
/// </summary>
internal sealed class AlertTrigger
{
    public long Id { get; set; }

    public Guid? PriceAlertId { get; set; }

    /// <summary>Denormalized from the alert so feed queries need no join.</summary>
    public Guid UserId { get; set; }

    public Guid ProductId { get; set; }

    public Guid OfferId { get; set; }

    public AlertKind Kind { get; set; }

    public DateTimeOffset TriggeredAt { get; set; }

    /// <summary>Effective prices (price plus shipping) before and after the change.</summary>
    public decimal? PriceBefore { get; set; }

    public decimal? PriceAfter { get; set; }

    /// <summary>A JSON object with the details of the event: seller, stock before and after, sizes that appeared.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>Set by the Notification feature once the trigger has been delivered.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}
