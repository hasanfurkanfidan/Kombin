using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

// MATCH SCORE ("%94 uyum") - rule-based, no ML. Read by the mobile team and by the later Recommendation/ML work.
//
//   score = round(100 * damping * min(raw, avoidedCap))
//
//   raw = 0.35 * color + 0.25 * formality + 0.15 * fit + 0.15 * season + 0.10 * family       (each component is 0..1)
//
//   color      ColorWeights[product color]; 0 when missing. A Multi product uses the average of the three strongest color
//              weights (a persona with fewer than three colors counts the missing ones as 0).
//   formality  1 - |product formality - FormalityMean| / 4, and 0 when the product lies outside the persona's
//              [FormalityMin, FormalityMax]. A persona that has liked nothing yet (FormalityMean 0) uses the middle of its range.
//   fit        FitWeights[product fit]; an Unknown fit is a neutral 0.5.
//   season     the sum of SeasonWeights over the seasons the product is sold in, capped at 1. A year-round product therefore
//              matches as well as the persona's strongest season.
//   family     FamilyWeights[derived family]; see ProductFamilies for how a product gets a family.
//
//   avoidedCap 0.25 when the product's color is in the persona's AvoidedColors, otherwise 1 (no cap).
//   damping    0.6 + 0.4 * Confidence. A persona without swipes can therefore never show more than 60: nothing has been learned yet.

/// <summary>The product facts the score looks at; <c>CategorySlug</c> is the product's own (possibly nested) category slug.</summary>
internal readonly record struct ProductTraits(
    ProductColor Color,
    int Formality,
    ProductFit Fit,
    Season Seasons,
    ProductPattern Pattern,
    string CategorySlug)
{
    /// <summary>The product's <see cref="Product.Category"/> must be loaded.</summary>
    public static ProductTraits From(Product product) =>
        new(product.PrimaryColor, product.Formality, product.Fit, product.Seasons, product.Pattern, product.Category.Slug);
}

internal static class MatchScorer
{
    private const double ColorShare = 0.35;

    private const double FormalityShare = 0.25;

    private const double FitShare = 0.15;

    private const double SeasonShare = 0.15;

    private const double FamilyShare = 0.10;

    private const double AvoidedColorCap = 0.25;

    private const double UnknownFit = 0.5;

    private static readonly Season[] SingleSeasons = [Season.Spring, Season.Summer, Season.Autumn, Season.Winter];

    /// <summary>The product's <see cref="Product.Category"/> must be loaded.</summary>
    public static int Score(PersonaStyleProfile profile, Persona persona, Product product) =>
        Score(profile, persona, ProductTraits.From(product));

    public static int Score(PersonaStyleProfile profile, Persona persona, ProductTraits product)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(persona);

        var raw = (ColorShare * ColorScore(profile, product.Color))
            + (FormalityShare * FormalityScore(profile, persona, product.Formality))
            + (FitShare * FitScore(profile, product.Fit))
            + (SeasonShare * SeasonScore(profile, product.Seasons))
            + (FamilyShare * profile.FamilyWeights.GetValueOrDefault(ProductFamilies.Derive(product.CategorySlug, product.Formality, product.Pattern).ToString()));

        if (persona.AvoidedColors.Contains(product.Color.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            raw = Math.Min(raw, AvoidedColorCap);
        }

        var damping = 0.6 + (0.4 * Math.Clamp(profile.Confidence, 0, 1));

        return (int)Math.Round(Math.Clamp(raw * damping, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }

    private static double ColorScore(PersonaStyleProfile profile, ProductColor color)
    {
        if (color != ProductColor.Multi)
        {
            return profile.ColorWeights.GetValueOrDefault(color.ToString());
        }

        return profile.ColorWeights.Values.OrderDescending().Take(3).Sum() / 3;
    }

    private static double FormalityScore(PersonaStyleProfile profile, Persona persona, int formality)
    {
        if (formality < persona.FormalityMin || formality > persona.FormalityMax)
        {
            return 0;
        }

        var mean = profile.FormalityMean > 0 ? (double)profile.FormalityMean : (persona.FormalityMin + persona.FormalityMax) / 2.0;

        return Math.Max(0, 1 - (Math.Abs(formality - mean) / 4));
    }

    private static double FitScore(PersonaStyleProfile profile, ProductFit fit) =>
        fit == ProductFit.Unknown ? UnknownFit : profile.FitWeights.GetValueOrDefault(fit.ToString());

    private static double SeasonScore(PersonaStyleProfile profile, Season seasons) =>
        Math.Min(1, SingleSeasons.Where(season => seasons.HasFlag(season)).Sum(season => profile.SeasonWeights.GetValueOrDefault(season.ToString())));
}

/// <summary>
/// Products carry no style family, so one is guessed from what the catalog does know. The first matching rule wins:
/// <list type="number">
/// <item>formality 5 → Formal</item>
/// <item>formality 4 → Classic (a blazer, loafer or shirt at this level is the textbook classic)</item>
/// <item>sneaker: formality ≤ 2 → Sporty, otherwise Minimal (a clean, dressier sneaker)</item>
/// <item>printed and formality ≤ 2 → Street</item>
/// <item>jeans → Street</item>
/// <item>formality 1 → Sporty</item>
/// <item>boots → Edgy</item>
/// <item>skirt → Romantic</item>
/// <item>printed (formality 3) → Boho</item>
/// <item>striped or checked (formality 3) → Classic</item>
/// <item>anything else → Minimal</item>
/// </list>
/// </summary>
internal static class ProductFamilies
{
    public static StyleFamily Derive(string categorySlug, int formality, ProductPattern pattern)
    {
        var leaf = categorySlug[(categorySlug.LastIndexOf('/') + 1)..];

        return formality switch
        {
            >= 5 => StyleFamily.Formal,
            4 => StyleFamily.Classic,
            _ when leaf == "sneaker" => formality <= 2 ? StyleFamily.Sporty : StyleFamily.Minimal,
            _ when pattern == ProductPattern.Printed && formality <= 2 => StyleFamily.Street,
            _ when leaf == "jean" => StyleFamily.Street,
            <= 1 => StyleFamily.Sporty,
            _ when leaf == "bot" => StyleFamily.Edgy,
            _ when leaf == "etek" => StyleFamily.Romantic,
            _ when pattern == ProductPattern.Printed => StyleFamily.Boho,
            _ when pattern is ProductPattern.Striped or ProductPattern.Checked => StyleFamily.Classic,
            _ => StyleFamily.Minimal,
        };
    }
}
