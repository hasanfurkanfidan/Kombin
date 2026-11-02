using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>
/// What an occasion asks for: a formality band (pieces may sit one step outside it, the outfit's average may not), the formality it
/// aims at, how well each style family suits it (0..1, unlisted families 0), and optionally a shoe it requires.
/// </summary>
internal sealed record OccasionPreset(
    Occasion Occasion,
    int Min,
    int Max,
    double Target,
    IReadOnlyDictionary<StyleFamily, double> Families,
    string? RequiredShoeLeaf = null);

internal static class Occasions
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Office (3-4, aiming at 3.5): smart casual to business; classic and minimal pieces, nothing sporty or loud.</item>
    /// <item>Weekend (1-3, aiming at 2): relaxed; street, minimal and boho lead, a suit-level piece is out of place.</item>
    /// <item>Evening (3-5, aiming at 4): dressed up with an edge; edgy and romantic lead, sporty is out.</item>
    /// <item>Sport (1-2, aiming at 1): sportswear, and the shoes must be sneakers.</item>
    /// <item>Event (4-5, aiming at 4.5): a wedding, a gala; formal and classic.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyDictionary<Occasion, OccasionPreset> Presets = new Dictionary<Occasion, OccasionPreset>
    {
        [Occasion.Office] = new(Occasion.Office, 3, 4, 3.5, Families(
            (StyleFamily.Classic, 1.0), (StyleFamily.Minimal, 0.9), (StyleFamily.Formal, 0.6), (StyleFamily.Romantic, 0.5),
            (StyleFamily.Edgy, 0.3), (StyleFamily.Boho, 0.3), (StyleFamily.Street, 0.2))),
        [Occasion.Weekend] = new(Occasion.Weekend, 1, 3, 2, Families(
            (StyleFamily.Street, 1.0), (StyleFamily.Minimal, 0.9), (StyleFamily.Boho, 0.9), (StyleFamily.Sporty, 0.7),
            (StyleFamily.Romantic, 0.7), (StyleFamily.Edgy, 0.7), (StyleFamily.Classic, 0.6), (StyleFamily.Formal, 0.1))),
        [Occasion.Evening] = new(Occasion.Evening, 3, 5, 4, Families(
            (StyleFamily.Edgy, 1.0), (StyleFamily.Romantic, 1.0), (StyleFamily.Minimal, 0.9), (StyleFamily.Classic, 0.8),
            (StyleFamily.Formal, 0.8), (StyleFamily.Boho, 0.6), (StyleFamily.Street, 0.4))),
        [Occasion.Sport] = new(Occasion.Sport, 1, 2, 1, Families(
            (StyleFamily.Sporty, 1.0), (StyleFamily.Street, 0.6), (StyleFamily.Minimal, 0.5)), RequiredShoeLeaf: "sneaker"),
        [Occasion.Event] = new(Occasion.Event, 4, 5, 4.5, Families(
            (StyleFamily.Formal, 1.0), (StyleFamily.Classic, 0.9), (StyleFamily.Romantic, 0.8), (StyleFamily.Minimal, 0.7),
            (StyleFamily.Edgy, 0.5))),
    };

    private static Dictionary<StyleFamily, double> Families(params (StyleFamily Family, double Weight)[] weights) =>
        weights.ToDictionary(weight => weight.Family, weight => weight.Weight);
}
