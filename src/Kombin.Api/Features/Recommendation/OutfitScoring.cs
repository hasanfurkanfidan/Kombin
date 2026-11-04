using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

// OUTFIT SCORE - rule-based, no ML. Read by the mobile team and by whoever tunes the engine; the rules themselves are in OutfitRules,
// WeatherRules and Occasions.
//
//   An outfit is a top, a bottom and shoes, plus an optional layer (outerwear) and an optional accessory.
//
//   REJECTED (never suggested) when, for any piece except the how-to-wear anchor:
//     - the weather bans it, or the layer is forbidden (hot day) / missing although required (cold or rainy day);
//     - its colour is one the persona avoids, or the occasion's required shoe is not worn;
//     - it sits more than one step outside the occasion's (or persona's) formality band, or the core's average lies more than
//       half a step outside it;
//     - and for the whole outfit when ColorRules, FormalityRules, SilhouetteRules or PatternRules reject it.
//
//   score = round(100 * (0.25 colour + 0.15 formality + 0.10 silhouette + 0.10 pattern + 0.10 weather + 0.10 occasion + 0.20 persona))
//           - 5 per wardrobe piece worn in the two days before the outfit's date (or on it)        ("you just wore that")
//           - 3 per catalog piece that is not the anchor                                          (prefer what you own)
//
//   colour, formality, silhouette, pattern   the rule tables' 0..1 scores.
//   weather     the average of WeatherRules.PieceFit over the pieces (season match, rain-proof shoes).
//   occasion    0.5 * the weight of the outfit's style family + 0.5 * (1 - |core average formality - target| / 4). The family is the
//               most common ProductFamilies.Derive of the pieces. Weights and target come from the occasion preset, or without an
//               occasion from the persona (FamilyWeights, FormalityMean); a persona that learned nothing weighs every family 0.5.
//   persona     the average MatchScorer score of the pieces / 100: what the persona's swipes say about each piece.
//
//   AcceptanceThreshold (60) is the line between "wearable" and "not worth showing"; possibleOutfits counts outfits above it.
//
//   RuleScore is the first four components alone, renormalised to 0-100: what describes the clothes, whoever wears them and whatever the
//   weather. OutfitCounter counts with it; OutfitPairs scores two pieces with it, and WithPersona adds the persona back at its own share.

/// <summary>What a persona contributes to an outfit's context; <see cref="None"/> for a user without personas.</summary>
internal sealed record PersonaTaste(
    int FormalityMin,
    int FormalityMax,
    double FormalityMean,
    IReadOnlyDictionary<StyleFamily, double> Families,
    IReadOnlySet<ProductColor> AvoidedColors,
    decimal? BudgetMin,
    decimal? BudgetMax)
{
    public static readonly PersonaTaste None = new(1, 5, 0, new Dictionary<StyleFamily, double>(), new HashSet<ProductColor>(), null, null);

    public static PersonaTaste From(Persona persona, PersonaStyleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(profile);

        var families = profile.FamilyWeights
            .Select(weight => (Parsed: Enum.TryParse<StyleFamily>(weight.Key, ignoreCase: true, out var family), Family: family, weight.Value))
            .Where(weight => weight.Parsed)
            .ToDictionary(weight => weight.Family, weight => weight.Value);
        var avoided = persona.AvoidedColors
            .Select(name => Enum.TryParse<ProductColor>(name, ignoreCase: true, out var colour) ? (ProductColor?)colour : null)
            .OfType<ProductColor>()
            .ToHashSet();

        return new PersonaTaste(
            Math.Min(persona.FormalityMin, persona.FormalityMax),
            Math.Max(persona.FormalityMin, persona.FormalityMax),
            (double)profile.FormalityMean,
            families,
            avoided,
            persona.BudgetMin,
            persona.BudgetMax);
    }
}

/// <summary>Everything about the day and the person that the score looks at, besides the pieces themselves.</summary>
internal sealed record OutfitContext(
    int BandMin,
    int BandMax,
    double FormalityTarget,
    Occasion? Occasion,
    IReadOnlyDictionary<StyleFamily, double> FamilyWeights,
    WeatherConditions Weather,
    DateOnly Date,
    IReadOnlySet<ProductColor> AvoidedColors,
    string? RequiredShoeLeaf)
{
    /// <summary>The occasion decides the band when there is one (an "event" is formal even for a casual persona); else the persona does.</summary>
    public static OutfitContext Create(Occasion? occasion, PersonaTaste taste, WeatherConditions weather, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(taste);
        ArgumentNullException.ThrowIfNull(weather);

        if (occasion is { } chosen)
        {
            var preset = Occasions.Presets[chosen];

            return new OutfitContext(preset.Min, preset.Max, preset.Target, chosen, preset.Families, weather, date, taste.AvoidedColors, preset.RequiredShoeLeaf);
        }

        var target = taste.FormalityMean > 0 ? taste.FormalityMean : (taste.FormalityMin + taste.FormalityMax) / 2.0;

        return new OutfitContext(taste.FormalityMin, taste.FormalityMax, target, null, taste.Families, weather, date, taste.AvoidedColors, null);
    }
}

internal sealed record OutfitEvaluation(
    bool Allowed,
    int Score,
    OutfitRule Rule,
    double Color = 0,
    double Formality = 0,
    double Silhouette = 0,
    double Pattern = 0,
    double Weather = 0,
    double Occasion = 0,
    double Persona = 0)
{
    public static readonly OutfitEvaluation Rejected = new(false, 0, OutfitRule.AllNeutral);
}

internal static class OutfitScoring
{
    public const int AcceptanceThreshold = 60;

    public const double ColorShare = 0.25;

    public const double FormalityShare = 0.15;

    public const double SilhouetteShare = 0.10;

    public const double PatternShare = 0.10;

    public const double WeatherShare = 0.10;

    public const double OccasionShare = 0.10;

    public const double PersonaShare = 0.20;

    public const double RuleShares = ColorShare + FormalityShare + SilhouetteShare + PatternShare;

    public const int RecentlyWornPenalty = 5;

    public const int RecentlyWornDays = 2;

    public const int GapPenalty = 3;

    /// <summary>How far outside the band a single piece, and the core's average, may sit.</summary>
    public const int PieceBandReach = 1;

    public const double AverageBandReach = 0.5;

    /// <summary>Family weight for a persona that has learned nothing about families yet.</summary>
    public const double UnknownFamilyWeight = 0.5;

    /// <summary>
    /// Scores one outfit. <paramref name="pieces"/> must hold exactly one top, bottom and shoes and at most one outerwear and accessory.
    /// The piece with key <paramref name="anchorKey"/> (how-to-wear) is exempt from the per-piece rules: the user asked about it.
    /// </summary>
    public static OutfitEvaluation Evaluate(IReadOnlyList<OutfitPiece> pieces, OutfitContext context, string? anchorKey = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(context);

        var top = Single(pieces, OutfitSlot.Top);
        var bottom = Single(pieces, OutfitSlot.Bottom);
        var shoes = Single(pieces, OutfitSlot.Shoes);
        var outerwear = pieces.FirstOrDefault(piece => piece.Slot == OutfitSlot.Outerwear);

        if (top is null || bottom is null || shoes is null)
        {
            return OutfitEvaluation.Rejected;
        }

        bool IsAnchor(OutfitPiece piece) => anchorKey is not null && piece.Key == anchorKey;

        switch (context.Weather.Outerwear)
        {
            case OuterwearPolicy.Forbidden when outerwear is not null && !IsAnchor(outerwear):
            case OuterwearPolicy.Required when outerwear is null:
                return OutfitEvaluation.Rejected;
        }

        if (context.RequiredShoeLeaf is { } shoe && shoes.Leaf != shoe && !IsAnchor(shoes))
        {
            return OutfitEvaluation.Rejected;
        }

        var weatherFits = new List<double>(pieces.Count);
        foreach (var piece in pieces)
        {
            var fit = WeatherRules.PieceFit(piece, context.Weather);

            if (!IsAnchor(piece))
            {
                if (fit is null
                    || context.AvoidedColors.Contains(piece.Color)
                    || piece.Formality < context.BandMin - PieceBandReach
                    || piece.Formality > context.BandMax + PieceBandReach)
                {
                    return OutfitEvaluation.Rejected;
                }
            }

            weatherFits.Add(fit ?? WeatherRules.OffSeasonScore);
        }

        var core = pieces.Where(piece => piece.Slot != OutfitSlot.Outerwear).ToList();
        var coreAverage = core.Average(piece => piece.Formality);
        if (coreAverage < context.BandMin - AverageBandReach || coreAverage > context.BandMax + AverageBandReach)
        {
            return OutfitEvaluation.Rejected;
        }

        var formality = FormalityRules.Judge([.. core.Select(piece => piece.Formality)], outerwear?.Formality);
        var colour = ColorRules.Judge(pieces);
        var silhouette = SilhouetteRules.Judge(top.Fit, bottom.Fit);
        var pattern = PatternRules.Judge(pieces);

        if (!formality.Allowed || !colour.Allowed || !silhouette.Allowed || !pattern.Allowed)
        {
            return OutfitEvaluation.Rejected;
        }

        var weather = weatherFits.Average();
        var occasion = (0.5 * FamilyWeight(pieces, context)) + (0.5 * Math.Max(0, 1 - (Math.Abs(coreAverage - context.FormalityTarget) / 4)));
        var persona = pieces.Average(piece => piece.PersonaScore) / 100.0;

        var raw = (ColorShare * colour.Score)
            + (FormalityShare * formality.Score)
            + (SilhouetteShare * silhouette.Score)
            + (PatternShare * pattern.Score)
            + (WeatherShare * weather)
            + (OccasionShare * occasion)
            + (PersonaShare * persona);

        var recentlyWorn = pieces.Count(piece => piece.LastWornAt is { } day && day <= context.Date && day >= context.Date.AddDays(-RecentlyWornDays));
        var gaps = pieces.Count(piece => piece.Source == PieceSource.Catalog && !IsAnchor(piece));
        var score = (int)Math.Round(raw * 100, MidpointRounding.AwayFromZero) - (RecentlyWornPenalty * recentlyWorn) - (GapPenalty * gaps);

        return new OutfitEvaluation(
            true,
            Math.Clamp(score, 0, 100),
            Explain(pieces, colour, context, top, bottom, outerwear, gaps > 0, anchorKey is not null),
            colour.Score,
            formality.Score,
            silhouette.Score,
            pattern.Score,
            weather,
            occasion,
            persona);
    }

    /// <summary>The rule components (0..1 each) alone, as a score of 0-100.</summary>
    public static int RuleScore(double colour, double formality, double silhouette, double pattern)
    {
        var raw = (ColorShare * colour) + (FormalityShare * formality) + (SilhouetteShare * silhouette) + (PatternShare * pattern);

        return (int)Math.Round(raw / RuleShares * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>A <see cref="RuleScore"/> with a <see cref="MatchScorer"/> score mixed in at their shares of the outfit score.</summary>
    public static int WithPersona(int ruleScore, int personaScore) =>
        (int)Math.Round(((RuleShares * ruleScore) + (PersonaShare * personaScore)) / (RuleShares + PersonaShare), MidpointRounding.AwayFromZero);

    /// <summary>The most telling reason the outfit works, in the priority of <see cref="OutfitRule"/>: weather first, then the gap, colour, cut and occasion.</summary>
    private static OutfitRule Explain(
        IReadOnlyList<OutfitPiece> pieces, ColorVerdict colour, OutfitContext context, OutfitPiece top, OutfitPiece bottom, OutfitPiece? outerwear, bool hasGap, bool hasAnchor)
    {
        var weather = context.Weather;

        if (weather.Rain && outerwear is not null)
        {
            return OutfitRule.Rain;
        }

        if (weather.Band is TemperatureBand.Cool or TemperatureBand.Cold && outerwear is not null)
        {
            return OutfitRule.Cold;
        }

        if (weather.Band == TemperatureBand.Hot)
        {
            return OutfitRule.Hot;
        }

        if (hasGap && !hasAnchor)
        {
            return OutfitRule.Gap;
        }

        switch (colour.Harmony)
        {
            case ColorHarmony.Complementary:
                return OutfitRule.Complementary;
            case ColorHarmony.Analogous:
                return OutfitRule.Analogous;
            case ColorHarmony.Monochrome:
                return OutfitRule.Monochrome;
            case ColorHarmony.OneStatement:
                return OutfitRule.OneStatement;
        }

        if (SilhouetteRules.IsBalancedVolume(top.Fit, bottom.Fit))
        {
            return OutfitRule.Silhouette;
        }

        if (pieces.Count(piece => PatternRules.Strong.Contains(piece.Pattern)) == 2)
        {
            return OutfitRule.SmallPatterns;
        }

        if (hasAnchor)
        {
            return OutfitRule.Anchor;
        }

        if (context.Occasion is { } occasion)
        {
            return occasion switch
            {
                Occasion.Office => OutfitRule.Office,
                Occasion.Weekend => OutfitRule.Weekend,
                Occasion.Evening => OutfitRule.Evening,
                Occasion.Sport => OutfitRule.Sport,
                _ => OutfitRule.Event,
            };
        }

        return colour.Harmony == ColorHarmony.QuietNeutrals ? OutfitRule.QuietNeutrals : OutfitRule.AllNeutral;
    }

    /// <summary>The weight of the outfit's most common family; ties go to the family listed first in <see cref="StyleFamily"/>.</summary>
    private static double FamilyWeight(IReadOnlyList<OutfitPiece> pieces, OutfitContext context)
    {
        if (context.FamilyWeights.Count == 0)
        {
            return UnknownFamilyWeight;
        }

        var family = pieces
            .Select(piece => ProductFamilies.Derive(piece.CategorySlug, piece.Formality, piece.Pattern))
            .GroupBy(candidate => candidate)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First()
            .Key;

        return context.FamilyWeights.GetValueOrDefault(family);
    }

    private static OutfitPiece? Single(IReadOnlyList<OutfitPiece> pieces, OutfitSlot slot)
    {
        OutfitPiece? found = null;

        foreach (var piece in pieces)
        {
            if (piece.Slot != slot)
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = piece;
        }

        return found;
    }
}
