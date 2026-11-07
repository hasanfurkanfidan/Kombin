namespace Kombin.Api.Persistence.Entities.Engagement;

/// <summary>Append-only record of one outbound click through <c>/go/{offerId}</c>, the revenue path.</summary>
internal sealed class Click
{
    public long Id { get; set; }

    /// <summary>Null for anonymous clicks, and after the user's account is deleted.</summary>
    public Guid? UserId { get; set; }

    public Guid OfferId { get; set; }

    public Guid ProductId { get; set; }

    public Guid SellerId { get; set; }

    /// <summary>The screen or placement the click came from, one of <c>ClickSources</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The active style persona; there is no table to reference yet.</summary>
    public Guid? PersonaId { get; set; }

    /// <summary>The social post the click came from (<c>/go?post=</c>), when it tags the clicked product. No foreign key: a deleted post must not take revenue records with it.</summary>
    public Guid? PostId { get; set; }

    /// <summary>The post's author when they are an active creator and not the clicker: who earns a share of the commission.</summary>
    public Guid? CreatorId { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string? UserAgent { get; set; }

    /// <summary>SHA-256 of the caller's IP and a configured salt; the raw IP is never stored.</summary>
    public string? IpHash { get; set; }

    /// <summary>The final URL the user was sent to, affiliate parameters included.</summary>
    public string RedirectUrl { get; set; } = string.Empty;

    public DateTimeOffset ClickedAt { get; set; }
}
