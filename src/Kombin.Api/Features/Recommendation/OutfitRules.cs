using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

// OUTFIT RULES - what makes pieces go together. Written as tables so a stylist can read them and argue with them; every entry says
// why in plain language. A rule either REJECTS a combination (it is never suggested) or SCORES it 0..1 (OutfitScoring weighs the
// scores). Change a table here, and its unit tests (OutfitRulesTests) with it.

/// <summary>The reason an outfit works, the subject of its one-line narration. The first that applies wins (see <c>OutfitScoring.Explain</c>).</summary>
internal enum OutfitRule
{
    Rain,
    Cold,
    Hot,
    Gap,
    Complementary,
    Analogous,
    Monochrome,
    OneStatement,
    Silhouette,
    SmallPatterns,
    Anchor,
    Office,
    Weekend,
    Evening,
    Sport,
    Event,
    QuietNeutrals,
    AllNeutral,
}

internal enum ColorHarmony
{
    AllNeutral,
    QuietNeutrals,
    OneStatement,
    Monochrome,
    Analogous,
    Complementary,
}

/// <summary>Allowed (with a 0..1 score) or rejected. <c>Statement</c>/<c>Second</c> are the non-neutral colours, if any.</summary>
internal readonly record struct ColorVerdict(bool Allowed, double Score, ColorHarmony Harmony, ProductColor? Statement = null, ProductColor? Second = null)
{
    public static readonly ColorVerdict Rejected = new(false, 0, ColorHarmony.AllNeutral);
}

internal static class ColorRules
{
    // Scores of the harmonies. One statement colour on a neutral base is the textbook outfit, so it is the best score; all neutrals
    // are always safe but a little flat; two colours only work as a known pair.
    public const double AllNeutralScore = 0.85;

    public const double OneStatementScore = 1.0;

    /// <summary>Several pieces in the same colour (tonal dressing): one colour, so as calm as a statement, and it elongates.</summary>
    public const double MonochromeScore = 0.95;

    public const double AnalogousScore = 0.8;

    public const double ComplementaryScore = 0.75;

    /// <summary>
    /// Neutral pairs that are allowed but read quieter or muddier than the rest: black with navy (both dark, they can look like a
    /// mismatch) and black with brown (the old "never" rule; fine today, but not the strongest pairing).
    /// </summary>
    public const double QuietNeutralsScore = 0.7;

    private static readonly (ProductColor A, ProductColor B)[] QuietNeutralPairs =
    [
        (ProductColor.Black, ProductColor.Navy),
        (ProductColor.Black, ProductColor.Brown),
    ];

    /// <summary>
    /// Neighbours on the colour wheel: they share a pigment, so they sit together without fighting (red-orange, yellow-green ...).
    /// Purple-pink and pink-red count as neighbours too: pink is a light red.
    /// </summary>
    private static readonly (ProductColor A, ProductColor B)[] AnalogousPairs =
    [
        (ProductColor.Red, ProductColor.Orange),
        (ProductColor.Orange, ProductColor.Yellow),
        (ProductColor.Yellow, ProductColor.Green),
        (ProductColor.Green, ProductColor.Blue),
        (ProductColor.Blue, ProductColor.Purple),
        (ProductColor.Purple, ProductColor.Pink),
        (ProductColor.Pink, ProductColor.Red),
    ];

    /// <summary>
    /// Opposites on the colour wheel: high contrast that makes both colours look brighter. Pink-green is the softer red-green.
    /// </summary>
    private static readonly (ProductColor A, ProductColor B)[] ComplementaryPairs =
    [
        (ProductColor.Blue, ProductColor.Orange),
        (ProductColor.Red, ProductColor.Green),
        (ProductColor.Yellow, ProductColor.Purple),
        (ProductColor.Pink, ProductColor.Green),
    ];

    /// <summary>
    /// Whether a piece counts as a neutral: the gap analysis' neutrals (black, white, grey, navy, beige, brown); blue denim, because
    /// jeans are the neutral of casual wear; and <c>Other</c>, a colour we could not name and therefore cannot judge.
    /// </summary>
    public static bool IsNeutral(OutfitPiece piece) => IsNeutral(piece.Color, piece.Leaf);

    /// <summary><paramref name="leaf"/> is the leaf of the category slug (<c>jean</c>).</summary>
    public static bool IsNeutral(ProductColor colour, string leaf) =>
        GapAnalysis.Neutrals.Contains(colour)
        || colour == ProductColor.Other
        || (colour == ProductColor.Blue && leaf == "jean");

    /// <summary>
    /// <list type="bullet">
    /// <item>no colour (all neutrals): allowed, <see cref="AllNeutralScore"/> (or <see cref="QuietNeutralsScore"/> for a quiet pair);</item>
    /// <item>one colour on one piece: the "one statement piece" rule, <see cref="OneStatementScore"/>;</item>
    /// <item>one colour on several pieces: tonal, <see cref="MonochromeScore"/>;</item>
    /// <item>two colours: only an analogous or complementary pair; <c>Multi</c> (a print) never pairs with another colour;</item>
    /// <item>three or more colours: rejected, the outfit has no focus.</item>
    /// </list>
    /// </summary>
    public static ColorVerdict Judge(IReadOnlyCollection<OutfitPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        var colours = pieces.Where(piece => !IsNeutral(piece)).Select(piece => piece.Color).ToList();
        var distinct = colours.Distinct().OrderBy(colour => colour).ToList();

        switch (distinct.Count)
        {
            case 0:
                var neutrals = pieces.Select(piece => piece.Color).ToHashSet();
                return QuietNeutralPairs.Any(pair => neutrals.Contains(pair.A) && neutrals.Contains(pair.B))
                    ? new ColorVerdict(true, QuietNeutralsScore, ColorHarmony.QuietNeutrals)
                    : new ColorVerdict(true, AllNeutralScore, ColorHarmony.AllNeutral);

            case 1:
                return colours.Count > 1
                    ? new ColorVerdict(true, MonochromeScore, ColorHarmony.Monochrome, distinct[0])
                    : new ColorVerdict(true, OneStatementScore, ColorHarmony.OneStatement, distinct[0]);

            case 2:
                var (first, second) = (distinct[0], distinct[1]);
                if (first == ProductColor.Multi || second == ProductColor.Multi)
                {
                    return ColorVerdict.Rejected;
                }

                if (Contains(AnalogousPairs, first, second))
                {
                    return new ColorVerdict(true, AnalogousScore, ColorHarmony.Analogous, first, second);
                }

                return Contains(ComplementaryPairs, first, second)
                    ? new ColorVerdict(true, ComplementaryScore, ColorHarmony.Complementary, first, second)
                    : ColorVerdict.Rejected;

            default:
                return ColorVerdict.Rejected;
        }
    }

    /// <summary>The colours a new piece may have to go with <paramref name="colour"/>: neutrals, the same colour and its listed partners.</summary>
    public static IReadOnlySet<ProductColor> PartnersOf(ProductColor colour)
    {
        var partners = new HashSet<ProductColor>(GapAnalysis.Neutrals);

        if (GapAnalysis.Neutrals.Contains(colour) || colour is ProductColor.Other or ProductColor.Multi)
        {
            return partners;
        }

        partners.Add(colour);
        foreach (var (a, b) in AnalogousPairs.Concat(ComplementaryPairs))
        {
            if (a == colour)
            {
                partners.Add(b);
            }
            else if (b == colour)
            {
                partners.Add(a);
            }
        }

        return partners;
    }

    private static bool Contains((ProductColor A, ProductColor B)[] pairs, ProductColor first, ProductColor second) =>
        pairs.Any(pair => (pair.A == first && pair.B == second) || (pair.A == second && pair.B == first));
}

internal static class FormalityRules
{
    /// <summary>
    /// The core pieces (top, bottom, shoes, accessory) may differ by at most one step: a suit trouser with a hoodie, or sneakers with
    /// a dress shirt and tailored trousers two steps up, reads as a mistake rather than a style.
    /// </summary>
    public const int MaxCoreSpread = 1;

    /// <summary>
    /// The layer may sit one step above the core (a blazer over a smart tee and jeans is the classic smart-casual move) or one below
    /// (a parka over office clothes on a cold morning); further apart it looks borrowed.
    /// </summary>
    public const int OuterwearReach = 1;

    /// <summary>All pieces on the same level look intentional; one step of spread is normal mixing.</summary>
    public const double SameLevelScore = 1.0;

    public const double OneStepScore = 0.85;

    public static (bool Allowed, double Score) Judge(IReadOnlyCollection<int> core, int? outerwear)
    {
        ArgumentNullException.ThrowIfNull(core);

        var min = core.Min();
        var max = core.Max();

        if (max - min > MaxCoreSpread)
        {
            return (false, 0);
        }

        if (outerwear is { } layer && (layer > max + OuterwearReach || layer < min - OuterwearReach))
        {
            return (false, 0);
        }

        return (true, max == min ? SameLevelScore : OneStepScore);
    }
}

internal static class SilhouetteRules
{
    /// <summary>
    /// Top fit (rows) × bottom fit (columns). Balance is the rule: volume on one half wants a closer line on the other.
    /// <list type="bullet">
    /// <item>oversized top + slim or regular bottom: the modern proportion, 1.0 / 0.95;</item>
    /// <item>slim top + relaxed or oversized bottom: the same balance turned around, 0.95 / 0.85;</item>
    /// <item>slim + slim: sleek, but can look tight, 0.8; regular + regular: unremarkable, 0.9;</item>
    /// <item>relaxed + relaxed: comfortable but shapeless, 0.75; relaxed/oversized together: 0.6;</item>
    /// <item>oversized + oversized: volume everywhere, rejected (0).</item>
    /// </list>
    /// An unknown fit on either side is a neutral <see cref="UnknownScore"/>.
    /// </summary>
    private static readonly Dictionary<(ProductFit Top, ProductFit Bottom), double> Table = new()
    {
        [(ProductFit.Slim, ProductFit.Slim)] = 0.8,
        [(ProductFit.Slim, ProductFit.Regular)] = 1.0,
        [(ProductFit.Slim, ProductFit.Relaxed)] = 0.95,
        [(ProductFit.Slim, ProductFit.Oversized)] = 0.85,
        [(ProductFit.Regular, ProductFit.Slim)] = 1.0,
        [(ProductFit.Regular, ProductFit.Regular)] = 0.9,
        [(ProductFit.Regular, ProductFit.Relaxed)] = 0.9,
        [(ProductFit.Regular, ProductFit.Oversized)] = 0.8,
        [(ProductFit.Relaxed, ProductFit.Slim)] = 0.95,
        [(ProductFit.Relaxed, ProductFit.Regular)] = 0.9,
        [(ProductFit.Relaxed, ProductFit.Relaxed)] = 0.75,
        [(ProductFit.Relaxed, ProductFit.Oversized)] = 0.6,
        [(ProductFit.Oversized, ProductFit.Slim)] = 1.0,
        [(ProductFit.Oversized, ProductFit.Regular)] = 0.95,
        [(ProductFit.Oversized, ProductFit.Relaxed)] = 0.6,
        [(ProductFit.Oversized, ProductFit.Oversized)] = 0,
    };

    public const double UnknownScore = 0.85;

    public static (bool Allowed, double Score) Judge(ProductFit top, ProductFit bottom)
    {
        if (top == ProductFit.Unknown || bottom == ProductFit.Unknown)
        {
            return (true, UnknownScore);
        }

        var score = Table[(top, bottom)];

        return (score > 0, score);
    }

    /// <summary>An oversized top over a slim or regular bottom: worth saying in the narration.</summary>
    public static bool IsBalancedVolume(ProductFit top, ProductFit bottom) =>
        top == ProductFit.Oversized && bottom is ProductFit.Slim or ProductFit.Regular;
}

internal static class PatternRules
{
    /// <summary>Patterns that draw the eye. Textured (a knit, a rib) is not one: it reads as a solid from a few steps away.</summary>
    public static readonly IReadOnlySet<ProductPattern> Strong = new HashSet<ProductPattern>
    {
        ProductPattern.Striped,
        ProductPattern.Checked,
        ProductPattern.Printed,
    };

    /// <summary>
    /// Pieces whose pattern is small-scale, so it can sit next to another small pattern (a pinstripe shirt under a small-check
    /// jacket is classic tailoring). We have no scale attribute yet, so this lists where a pattern is small in practice: striped or
    /// checked shirts (pinstripe, gingham) and accessories (a small area). Prints are never small-scale here.
    /// </summary>
    private static readonly HashSet<(string Leaf, ProductPattern Pattern)> SmallScale =
    [
        ("gomlek", ProductPattern.Striped),
        ("gomlek", ProductPattern.Checked),
    ];

    public const double NoPatternScore = 1.0;

    /// <summary>One pattern on a plain base: the pattern is the statement.</summary>
    public const double OnePatternScore = 1.0;

    /// <summary>Two small-scale patterns can work, but it is the riskier look.</summary>
    public const double SmallPatternsScore = 0.8;

    public static bool IsSmallScale(OutfitPiece piece) =>
        piece.Pattern != ProductPattern.Printed
        && (piece.Slot == OutfitSlot.Accessory || SmallScale.Contains((piece.Leaf, piece.Pattern)));

    /// <summary>At most one strong pattern, unless there are exactly two and both are small-scale. Three are always too many.</summary>
    public static (bool Allowed, double Score) Judge(IReadOnlyCollection<OutfitPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        var patterned = pieces.Where(piece => Strong.Contains(piece.Pattern)).ToList();

        return patterned.Count switch
        {
            0 => (true, NoPatternScore),
            1 => (true, OnePatternScore),
            2 when patterned.All(IsSmallScale) => (true, SmallPatternsScore),
            _ => (false, 0),
        };
    }
}
