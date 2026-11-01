namespace Kombin.Api.Persistence.Entities.Profile;

/// <summary>
/// What the persona's swipes and explicit preferences say about its taste. Derived data: recomputed by
/// <see cref="Features.Profile.StyleProfileBuilder"/> and never edited directly. Weights are 0-1, the strongest entry being 1;
/// keys with no weight are omitted.
/// </summary>
internal sealed class PersonaStyleProfile
{
    public Guid PersonaId { get; set; }

    /// <summary>Keyed by <see cref="Catalog.ProductColor"/> name.</summary>
    public Dictionary<string, double> ColorWeights { get; set; } = [];

    /// <summary>Keyed by <see cref="StyleFamily"/> name.</summary>
    public Dictionary<string, double> FamilyWeights { get; set; } = [];

    /// <summary>Keyed by <see cref="Catalog.ProductFit"/> name.</summary>
    public Dictionary<string, double> FitWeights { get; set; } = [];

    /// <summary>Mean formality of the liked cards; 0 while nothing has been liked.</summary>
    public decimal FormalityMean { get; set; }

    /// <summary>Keyed by single <see cref="Catalog.Season"/> name.</summary>
    public Dictionary<string, double> SeasonWeights { get; set; } = [];

    public int LikeCount { get; set; }

    public int DislikeCount { get; set; }

    /// <summary>0-1; <c>min(1, likes / 12)</c>.</summary>
    public double Confidence { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
