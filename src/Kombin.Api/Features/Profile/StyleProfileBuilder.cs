using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

/// <summary>One swipe together with the card it was made on.</summary>
internal readonly record struct StyleSwipeSample(SwipeDecision Decision, StyleCard Card);

/// <summary>What <see cref="StyleProfileBuilder"/> derives; copied onto <see cref="PersonaStyleProfile"/> by <see cref="StyleProfiles.Apply"/>.</summary>
internal sealed record StyleProfileData(
    Dictionary<string, double> ColorWeights,
    Dictionary<string, double> FamilyWeights,
    Dictionary<string, double> FitWeights,
    decimal FormalityMean,
    Dictionary<string, double> SeasonWeights,
    int LikeCount,
    int DislikeCount,
    double Confidence);

/// <summary>
/// Turns a persona's swipes and explicit preferences into its <see cref="PersonaStyleProfile"/>. Pure: same input, same output.
/// <para>
/// For each dimension (color, family, fit, season) every attribute value of a card counts once. A like adds 1, a dislike
/// subtracts 0.5, a skip counts for nothing. The sum per value is floored at 0. Then the persona's explicit preferences are laid
/// over it: each <see cref="Persona.PreferredColors"/> (and, for fits, <see cref="Persona.PreferredFits"/>) adds 0.5 and each
/// <see cref="Persona.AvoidedColors"/> is set to 0. Finally the values are divided by the largest one, so the strongest value is 1
/// and values without any weight are left out. With no signal at all every dictionary is empty.
/// </para>
/// <para>
/// <c>FormalityMean</c> is the mean formality of the liked cards (0 when there are none). <c>Confidence</c> is
/// <c>min(1, likes / 12)</c>.
/// </para>
/// </summary>
internal static class StyleProfileBuilder
{
    public const int FullConfidenceLikes = 12;

    private const double LikeWeight = 1.0;

    private const double DislikeWeight = -0.5;

    private const double ExplicitPreferenceBonus = 0.5;

    private static readonly Season[] SingleSeasons = [Season.Spring, Season.Summer, Season.Autumn, Season.Winter];

    public static StyleProfileData Build(IEnumerable<StyleSwipeSample> swipes, Persona persona)
    {
        ArgumentNullException.ThrowIfNull(swipes);
        ArgumentNullException.ThrowIfNull(persona);

        Dictionary<string, double> colors = [];
        Dictionary<string, double> families = [];
        Dictionary<string, double> fits = [];
        Dictionary<string, double> seasons = [];
        var likes = 0;
        var dislikes = 0;
        var likedFormality = 0;

        foreach (var (decision, card) in swipes)
        {
            double weight;

            switch (decision)
            {
                case SwipeDecision.Like:
                    likes++;
                    likedFormality += card.Formality;
                    weight = LikeWeight;
                    break;
                case SwipeDecision.Dislike:
                    dislikes++;
                    weight = DislikeWeight;
                    break;
                default:
                    continue;
            }

            foreach (var color in card.Colors)
            {
                Add(colors, color, weight);
            }

            Add(families, card.Family.ToString(), weight);

            foreach (var fit in card.Fits)
            {
                Add(fits, fit, weight);
            }

            foreach (var season in SingleSeasons.Where(season => card.Seasons.HasFlag(season)))
            {
                Add(seasons, season.ToString(), weight);
            }
        }

        FloorAtZero(colors);
        FloorAtZero(fits);

        foreach (var color in persona.PreferredColors)
        {
            Add(colors, color, ExplicitPreferenceBonus);
        }

        foreach (var fit in persona.PreferredFits)
        {
            Add(fits, fit, ExplicitPreferenceBonus);
        }

        foreach (var color in persona.AvoidedColors)
        {
            colors[color] = 0;
        }

        return new StyleProfileData(
            Normalize(colors),
            Normalize(families),
            Normalize(fits),
            likes == 0 ? 0m : Math.Round((decimal)likedFormality / likes, 2, MidpointRounding.AwayFromZero),
            Normalize(seasons),
            likes,
            dislikes,
            Math.Min(1.0, (double)likes / FullConfidenceLikes));
    }

    private static void Add(Dictionary<string, double> weights, string key, double amount) =>
        weights[key] = weights.GetValueOrDefault(key) + amount;

    private static void FloorAtZero(Dictionary<string, double> weights)
    {
        foreach (var key in weights.Keys.ToList())
        {
            weights[key] = Math.Max(0, weights[key]);
        }
    }

    /// <summary>Families and seasons are floored here; colors and fits were floored before the explicit preferences were added.</summary>
    private static Dictionary<string, double> Normalize(Dictionary<string, double> weights)
    {
        var positive = weights.Where(pair => pair.Value > 0).ToList();

        if (positive.Count == 0)
        {
            return [];
        }

        var max = positive.Max(pair => pair.Value);

        return positive.ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value / max, 4));
    }
}
