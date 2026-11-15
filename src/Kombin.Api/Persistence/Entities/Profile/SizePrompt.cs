namespace Kombin.Api.Persistence.Entities.Profile;

/// <summary>How the user answered "Aldın mı? Beden uydu mu?".</summary>
internal enum SizePromptAnswer
{
    /// <summary>Bought it and the size fitted: the answer carries the size.</summary>
    BoughtFits,

    BoughtTooSmall,

    BoughtTooLarge,

    NotBought,

    /// <summary>"Bir daha sorma" for this product.</summary>
    NeverAsk,
}

/// <summary>
/// One "did you buy it, did the size fit?" question about a product the user left the app for (<c>/go</c>). Created by
/// <c>GET /me/size-prompts</c> from the user's first click on the product, never by <c>/go</c> itself; one per (user, product), ever. The click,
/// offer and seller are remembered without foreign keys: the click is the revenue record and already guards them. Personal data: it cascades
/// with the user.
/// </summary>
internal sealed class SizePrompt : BaseEntity
{
    public Guid UserId { get; set; }

    public Guid ProductId { get; set; }

    public Guid SellerId { get; set; }

    public Guid OfferId { get; set; }

    public long ClickId { get; set; }

    /// <summary>When the user left; the question opens 3 days later and expires 30 days after this instant.</summary>
    public DateTimeOffset ClickedAt { get; set; }

    /// <summary>Null while the question is open.</summary>
    public SizePromptAnswer? Answer { get; set; }

    /// <summary>The size named in the answer (normalised like <c>SizeEntry.Size</c>), when there was one.</summary>
    public string? AnsweredSize { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }
}
