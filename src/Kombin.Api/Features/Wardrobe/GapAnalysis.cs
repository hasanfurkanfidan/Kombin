using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Wardrobe;

// GAP ANALYSIS ("Dolabında bej tonlarda üst yok") - rule-based, no ML. Read by the mobile team and by the Recommendation work.
//
//   pieces    the user's non-archived items that have a category, grouped by top-level category (SizeGroups): Tops, Bottoms,
//             Outerwear, Shoes. Accessories are ignored.
//   bands     formality 1-2 Casual ("günlük"), 3 Smart ("smart casual"), 4-5 Formal ("resmi"). The persona's bands are those its
//             [FormalityMin, FormalityMax] touches; without a persona, Casual and Smart.
//   neutral   Black, White, Grey, Navy, Beige, Brown: the colors that combine with anything.
//
//   For every persona band, and in it for Tops, Bottoms and Shoes:
//     missing_{band}_{group}    no piece of the group in the band at all       "Dolabında hiç resmi ayakkabı yok"
//     missing_neutral_{group}   pieces in the band, but none of them neutral   "Dolabında bej tonlarda üst yok"
//   And once, when the persona's seasons include Autumn or Winter:
//     missing_outerwear         no outerwear at all                            "Dolabında soğuk günler için dış giyim yok"
//
//   The suggested piece is always neutral: the persona's first preferred neutral color that it does not avoid (its explicit preferred
//   colors first, then the colors it learned from swipes, strongest first), otherwise the first non-avoided color of the group's
//   default order (DefaultNeutrals). Its formality is the band's center (Casual 2, Smart 3,
//   Formal 4; outerwear takes the first band's).
//
//   unlocksEstimate = the outfits the piece would complete: bottoms × shoes for a top, tops × shoes for a bottom, tops × bottoms for
//   shoes and for outerwear, counting only pieces whose formality is within 1 of the suggested piece's. Colors are not checked: a
//   neutral piece goes with every color.
//
//   Order: unlocksEstimate (descending), then missing_{band}_* before missing_neutral_* before missing_outerwear, then band
//   (Casual, Smart, Formal), then group (Tops, Bottoms, Shoes). At most MaxGaps. The band adjective appears in a missing_neutral
//   message only when the persona spans several bands, where it tells two such gaps apart.

internal enum FormalityBand
{
    Casual,
    Smart,
    Formal,
}

/// <summary>One wardrobe piece as the gap analysis sees it.</summary>
internal readonly record struct GapPiece(SizeGroup Group, ProductColor Color, int Formality);

/// <summary>What the analysis needs of a persona. <c>PreferredColors</c> are in order of preference.</summary>
internal sealed record GapPersona(int FormalityMin, int FormalityMax, IReadOnlyList<ProductColor> PreferredColors, IReadOnlyCollection<ProductColor> AvoidedColors, Season Seasons)
{
    /// <summary>The stand-in for a user without a persona: casual to smart, no color preferences, all year.</summary>
    public static readonly GapPersona Default = new(1, 3, [], [], Season.AllYear);

    /// <summary>The explicit preferred colors come first, then what <paramref name="styleProfile"/> learned (positive weights, strongest first).</summary>
    public static GapPersona From(Persona persona, PersonaStyleProfile? styleProfile)
    {
        ArgumentNullException.ThrowIfNull(persona);

        var learned = styleProfile?.ColorWeights.Where(weight => weight.Value > 0).OrderByDescending(weight => weight.Value).Select(weight => weight.Key) ?? [];

        return new GapPersona(
            persona.FormalityMin,
            persona.FormalityMax,
            [.. ParseColors(persona.PreferredColors.Concat(learned)).Distinct()],
            ParseColors(persona.AvoidedColors),
            persona.Seasons);
    }

    private static ProductColor[] ParseColors(IEnumerable<string> names) =>
        [.. names.Select(name => Enum.TryParse<ProductColor>(name, ignoreCase: true, out var color) ? (ProductColor?)color : null).OfType<ProductColor>()];
}

/// <summary><c>CategorySlug</c> is the top-level category of the missing piece (<c>ust-giyim</c>, ...).</summary>
internal sealed record WardrobeGap(string Kind, string Message, string CategorySlug, ProductColor Color, int Formality, int UnlocksEstimate)
{
    public FormalityBand Band => GapAnalysis.BandOf(Formality);
}

internal static class GapAnalysis
{
    public const int MaxGaps = 5;

    public static readonly IReadOnlySet<ProductColor> Neutrals = new HashSet<ProductColor>
    {
        ProductColor.Black,
        ProductColor.White,
        ProductColor.Grey,
        ProductColor.Navy,
        ProductColor.Beige,
        ProductColor.Brown,
    };

    private static readonly SizeGroup[] CoreGroups = [SizeGroup.Tops, SizeGroup.Bottoms, SizeGroup.Shoes];

    private static readonly Dictionary<SizeGroup, ProductColor[]> DefaultNeutrals = new()
    {
        [SizeGroup.Tops] = [ProductColor.White, ProductColor.Beige, ProductColor.Black, ProductColor.Grey, ProductColor.Navy, ProductColor.Brown],
        [SizeGroup.Bottoms] = [ProductColor.Navy, ProductColor.Black, ProductColor.Beige, ProductColor.Grey, ProductColor.Brown, ProductColor.White],
        [SizeGroup.Shoes] = [ProductColor.White, ProductColor.Black, ProductColor.Brown, ProductColor.Beige, ProductColor.Grey, ProductColor.Navy],
        [SizeGroup.Outerwear] = [ProductColor.Beige, ProductColor.Black, ProductColor.Navy, ProductColor.Brown, ProductColor.Grey, ProductColor.White],
    };

    private enum Rule
    {
        MissingInBand,
        MissingNeutral,
        MissingOuterwear,
    }

    public static IReadOnlyList<WardrobeGap> Analyze(IReadOnlyCollection<GapPiece> pieces, GapPersona? persona)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        persona ??= GapPersona.Default;
        var bands = BandsOf(persona);
        var multiBand = bands.Count > 1;
        var candidates = new List<(WardrobeGap Gap, Rule Rule, FormalityBand Band, int GroupOrder)>();

        foreach (var band in bands)
        {
            for (var groupOrder = 0; groupOrder < CoreGroups.Length; groupOrder++)
            {
                var group = CoreGroups[groupOrder];
                var inBand = pieces.Where(piece => piece.Group == group && BandOf(piece.Formality) == band).ToList();

                if (inBand.Count > 0 && inBand.Any(piece => Neutrals.Contains(piece.Color)))
                {
                    continue;
                }

                var rule = inBand.Count == 0 ? Rule.MissingInBand : Rule.MissingNeutral;
                var color = SuggestColor(group, persona);
                var formality = Center(band);
                var kind = rule == Rule.MissingInBand ? $"missing_{Token(band)}_{Token(group)}" : $"missing_neutral_{Token(group)}";
                var message = rule == Rule.MissingInBand
                    ? $"Dolabında hiç {Adjective(band)} {Noun(group)} yok"
                    : $"Dolabında {ColorName(color)} tonlarda {(multiBand ? $"{Adjective(band)} " : string.Empty)}{Noun(group)} yok";

                candidates.Add((new WardrobeGap(kind, message, RootSlug(group), color, formality, Unlocks(pieces, group, formality)), rule, band, groupOrder));
            }
        }

        if ((persona.Seasons & (Season.Autumn | Season.Winter)) != 0 && !pieces.Any(piece => piece.Group == SizeGroup.Outerwear))
        {
            var formality = Center(bands[0]);
            var color = SuggestColor(SizeGroup.Outerwear, persona);

            candidates.Add((
                new WardrobeGap("missing_outerwear", "Dolabında soğuk günler için dış giyim yok", RootSlug(SizeGroup.Outerwear), color, formality, Unlocks(pieces, SizeGroup.Outerwear, formality)),
                Rule.MissingOuterwear,
                bands[0],
                CoreGroups.Length));
        }

        return
        [
            .. candidates
                .OrderByDescending(candidate => candidate.Gap.UnlocksEstimate)
                .ThenBy(candidate => candidate.Rule)
                .ThenBy(candidate => candidate.Band)
                .ThenBy(candidate => candidate.GroupOrder)
                .Take(MaxGaps)
                .Select(candidate => candidate.Gap),
        ];
    }

    public static FormalityBand BandOf(int formality) => formality switch
    {
        <= 2 => FormalityBand.Casual,
        3 => FormalityBand.Smart,
        _ => FormalityBand.Formal,
    };

    /// <summary>The formality range of a band, for querying products that would fill a gap.</summary>
    public static (int Min, int Max) Range(FormalityBand band) => band switch
    {
        FormalityBand.Casual => (1, 2),
        FormalityBand.Smart => (3, 3),
        _ => (4, 5),
    };

    private static List<FormalityBand> BandsOf(GapPersona persona)
    {
        var min = Math.Clamp(Math.Min(persona.FormalityMin, persona.FormalityMax), 1, 5);
        var max = Math.Clamp(Math.Max(persona.FormalityMin, persona.FormalityMax), 1, 5);

        return [.. Enum.GetValues<FormalityBand>().Where(band => Range(band).Min <= max && Range(band).Max >= min)];
    }

    private static int Center(FormalityBand band) => band switch
    {
        FormalityBand.Casual => 2,
        FormalityBand.Smart => 3,
        _ => 4,
    };

    private static ProductColor SuggestColor(SizeGroup group, GapPersona persona)
    {
        foreach (var color in persona.PreferredColors)
        {
            if (Neutrals.Contains(color) && !persona.AvoidedColors.Contains(color))
            {
                return color;
            }
        }

        var order = DefaultNeutrals[group];

        // A persona that avoids every neutral still gets one: the group's first.
        return order.FirstOrDefault(color => !persona.AvoidedColors.Contains(color), order[0]);
    }

    /// <summary>The outfits a new piece of <paramref name="group"/> at <paramref name="formality"/> would complete with what is there.</summary>
    private static int Unlocks(IReadOnlyCollection<GapPiece> pieces, SizeGroup group, int formality)
    {
        int Count(SizeGroup other) => pieces.Count(piece => piece.Group == other && Math.Abs(piece.Formality - formality) <= 1);

        return group switch
        {
            SizeGroup.Tops => Count(SizeGroup.Bottoms) * Count(SizeGroup.Shoes),
            SizeGroup.Bottoms => Count(SizeGroup.Tops) * Count(SizeGroup.Shoes),
            _ => Count(SizeGroup.Tops) * Count(SizeGroup.Bottoms),
        };
    }

    private static string Token(FormalityBand band) => band.ToString().ToLowerInvariant();

    private static string Token(SizeGroup group) => group switch
    {
        SizeGroup.Tops => "top",
        SizeGroup.Bottoms => "bottom",
        SizeGroup.Shoes => "shoes",
        _ => "outerwear",
    };

    private static string Adjective(FormalityBand band) => band switch
    {
        FormalityBand.Casual => "günlük",
        FormalityBand.Smart => "smart casual",
        _ => "resmi",
    };

    private static string Noun(SizeGroup group) => group switch
    {
        SizeGroup.Tops => "üst",
        SizeGroup.Bottoms => "alt giyim",
        SizeGroup.Shoes => "ayakkabı",
        _ => "dış giyim",
    };

    public static string RootSlug(SizeGroup group) => group switch
    {
        SizeGroup.Tops => "ust-giyim",
        SizeGroup.Bottoms => "alt-giyim",
        SizeGroup.Shoes => "ayakkabi",
        SizeGroup.Outerwear => "dis-giyim",
        _ => "aksesuar",
    };

    public static string ColorName(ProductColor color) => color switch
    {
        ProductColor.Black => "siyah",
        ProductColor.White => "beyaz",
        ProductColor.Grey => "gri",
        ProductColor.Navy => "lacivert",
        ProductColor.Blue => "mavi",
        ProductColor.Green => "yeşil",
        ProductColor.Beige => "bej",
        ProductColor.Brown => "kahverengi",
        ProductColor.Red => "kırmızı",
        ProductColor.Pink => "pembe",
        ProductColor.Purple => "mor",
        ProductColor.Yellow => "sarı",
        ProductColor.Orange => "turuncu",
        ProductColor.Multi => "çok renkli",
        _ => "farklı",
    };
}
