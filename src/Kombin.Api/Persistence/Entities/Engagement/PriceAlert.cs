namespace Kombin.Api.Persistence.Entities.Engagement;

/// <summary>A user's request to be told about a product. Evaluated by <c>AlertEvaluator</c> whenever an offer of the product changes.</summary>
internal sealed class PriceAlert : BaseEntity
{
    public Guid UserId { get; set; }

    public Guid ProductId { get; set; }

    public AlertKind Kind { get; set; }

    /// <summary>Required for <see cref="AlertKind.PriceDrop"/> (compared with the effective price), forbidden otherwise.</summary>
    public decimal? TargetPrice { get; set; }

    /// <summary>When not empty, a <see cref="AlertKind.BackInStock"/> alert only fires when one of these sizes is available.</summary>
    public string[] Sizes { get; set; } = [];

    public bool IsActive { get; set; } = true;

    public DateTimeOffset? LastTriggeredAt { get; set; }

    public int TriggerCount { get; set; }
}
