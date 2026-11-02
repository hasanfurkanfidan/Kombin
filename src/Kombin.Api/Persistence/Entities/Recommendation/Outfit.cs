namespace Kombin.Api.Persistence.Entities.Recommendation;

/// <summary>
/// One outfit the engine suggested. Created by <c>GET /me/outfits/daily</c> and <c>/how-to-wear</c> (a repeated request with the same
/// inputs reuses the row with the same <see cref="Hash"/>); the user then saves, wears or dismisses it.
/// </summary>
internal sealed class Outfit : BaseEntity
{
    public const int MaxNarrationLength = 300;

    public Guid UserId { get; set; }

    /// <summary>The persona it was built for; null for a user without personas.</summary>
    public Guid? PersonaId { get; set; }

    public OutfitKind Kind { get; set; }

    public OutfitCompleteness Completeness { get; set; }

    public Occasion? Occasion { get; set; }

    /// <summary>The day it is meant for (Europe/Istanbul); today for how-to-wear.</summary>
    public DateOnly ForDate { get; set; }

    /// <summary>The weather it was built for, as JSON (<c>WeatherSnapshot</c>); null without a city or a forecast.</summary>
    public string? WeatherJson { get; set; }

    public int Seed { get; set; }

    /// <summary>0-100, see <c>OutfitScoring</c>.</summary>
    public int Score { get; set; }

    /// <summary>The rule that made it work (an <c>OutfitRule</c> name), which the narration talks about.</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>Identifies the set of pieces (<c>OutfitHash</c>); a dismissed hash is not suggested again for 30 days.</summary>
    public string Hash { get; set; } = string.Empty;

    public OutfitStatus Status { get; set; }

    public string Narration { get; set; } = string.Empty;

    public NarrationProvider NarrationProvider { get; set; }

    /// <summary>The narration cache key: the pieces plus what the line talks about (rule, occasion, weather).</summary>
    public string NarrationHash { get; set; } = string.Empty;

    /// <summary>What the narrator is told (<c>OutfitSummary</c> as JSON), kept so the background narration needs no other data.</summary>
    public string NarrationInput { get; set; } = string.Empty;

    public DateTimeOffset? SavedAt { get; set; }

    public DateTimeOffset? WornAt { get; set; }

    public DateTimeOffset? DismissedAt { get; set; }

    public DismissReason? DismissReason { get; set; }

    public List<OutfitItem> Items { get; } = [];
}
