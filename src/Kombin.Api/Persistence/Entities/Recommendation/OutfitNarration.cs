namespace Kombin.Api.Persistence.Entities.Recommendation;

/// <summary>A line an LLM narrator wrote, cached by narration hash so the same outfit is not narrated twice within 7 days.</summary>
internal sealed class OutfitNarration
{
    public string Hash { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
