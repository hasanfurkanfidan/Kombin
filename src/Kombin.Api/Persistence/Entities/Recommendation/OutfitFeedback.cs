namespace Kombin.Api.Persistence.Entities.Recommendation;

/// <summary>What the user did with an outfit. Append-only; the outfit's own status is the current state.</summary>
internal sealed class OutfitFeedback
{
    public long Id { get; set; }

    public Guid OutfitId { get; set; }

    public Guid UserId { get; set; }

    public OutfitFeedbackAction Action { get; set; }

    /// <summary>Only for <see cref="OutfitFeedbackAction.Dismiss"/>.</summary>
    public DismissReason? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
