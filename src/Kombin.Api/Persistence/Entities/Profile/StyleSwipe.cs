namespace Kombin.Api.Persistence.Entities.Profile;

/// <summary>A persona's decision on one style card. One row per (persona, card); swiping again replaces the decision.</summary>
internal sealed class StyleSwipe
{
    public long Id { get; set; }

    public Guid PersonaId { get; set; }

    public Guid StyleCardId { get; set; }

    public SwipeDecision Decision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
