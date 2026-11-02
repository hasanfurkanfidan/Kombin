using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Recommendation.Narration;

/// <summary>
/// The little Turkish grammar the outfit lines need: garment nouns, the instrumental "ile" as a suffix ("pantolonla", "gömlekle",
/// "trikoyla"), and Turkish capitalisation. Colours reuse <see cref="GapAnalysis.ColorName"/>, lower-casing reuses
/// <see cref="Copy.TurkishLower"/>.
/// </summary>
internal static class TurkishGrammar
{
    /// <summary>
    /// Garment nouns by category leaf, with their "ile" form written out: loanwords follow their pronunciation, not their spelling
    /// ("blazer" is said "bleyzır", so "blazer'la"), which no suffix rule could guess.
    /// </summary>
    private static readonly Dictionary<string, (string Noun, string With)> Nouns = new(StringComparer.Ordinal)
    {
        ["tisort"] = ("tişört", "tişörtle"),
        ["gomlek"] = ("gömlek", "gömlekle"),
        ["triko"] = ("triko", "trikoyla"),
        ["blazer"] = ("blazer", "blazer'la"),
        ["pantolon"] = ("pantolon", "pantolonla"),
        ["jean"] = ("jean", "jean'le"),
        ["etek"] = ("etek", "etekle"),
        ["mont"] = ("mont", "montla"),
        ["kaban"] = ("kaban", "kabanla"),
        ["ceket"] = ("ceket", "ceketle"),
        ["sneaker"] = ("sneaker", "sneaker'la"),
        ["bot"] = ("bot", "botla"),
        ["loafer"] = ("loafer", "loafer'la"),
        ["canta"] = ("çanta", "çantayla"),
        ["kemer"] = ("kemer", "kemerle"),
        ["ust-giyim"] = ("üst", "üstle"),
        ["alt-giyim"] = ("alt", "altla"),
        ["dis-giyim"] = ("dış giyim", "dış giyimle"),
        ["ayakkabi"] = ("ayakkabı", "ayakkabıyla"),
        ["aksesuar"] = ("aksesuar", "aksesuarla"),
    };

    private const string BackVowels = "aıou";

    private const string FrontVowels = "eiöü";

    public static string Noun(string categorySlug) => Forms(categorySlug).Noun;

    public static string NounWith(string categorySlug) => Forms(categorySlug).With;

    /// <summary>
    /// The instrumental suffix by vowel harmony, for words outside the table: the last vowel decides -la (a, ı, o, u) or -le (e, i, ö,
    /// ü), and a final vowel takes a buffer y ("çanta" → "çantayla").
    /// </summary>
    public static string WithSuffix(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var lower = Copy.TurkishLower(word);
        var lastVowel = lower.LastOrDefault(character => BackVowels.Contains(character, StringComparison.Ordinal) || FrontVowels.Contains(character, StringComparison.Ordinal));
        var suffix = BackVowels.Contains(lastVowel, StringComparison.Ordinal) ? "la" : "le";
        var buffer = lower.Length > 0 && (BackVowels + FrontVowels).Contains(lower[^1], StringComparison.Ordinal) ? "y" : string.Empty;

        return word + buffer + suffix;
    }

    /// <summary>The adjective of a colour; empty for <see cref="ProductColor.Other"/>, which has no good name.</summary>
    public static string Colour(ProductColor colour) => colour == ProductColor.Other ? string.Empty : GapAnalysis.ColorName(colour);

    /// <summary>"lacivert pantolon".</summary>
    public static string Phrase(ProductColor colour, string categorySlug) => Join(Colour(colour), Noun(categorySlug));

    /// <summary>"lacivert pantolonla".</summary>
    public static string PhraseWith(ProductColor colour, string categorySlug) => Join(Colour(colour), NounWith(categorySlug));

    /// <summary>Upper-cases the first letter the Turkish way ("ilk" → "İlk", "ıhlamur" → "Ihlamur").</summary>
    public static string Capitalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return text;
        }

        var first = text[0] switch
        {
            'i' => 'İ',
            'ı' => 'I',
            var other => char.ToUpperInvariant(other),
        };

        return first + text[1..];
    }

    private static (string Noun, string With) Forms(string categorySlug)
    {
        var leaf = categorySlug[(categorySlug.LastIndexOf('/') + 1)..];

        if (Nouns.TryGetValue(leaf, out var forms))
        {
            return forms;
        }

        var root = categorySlug.Split('/')[0];

        return Nouns.TryGetValue(root, out var rootForms) ? rootForms : (leaf, WithSuffix(leaf));
    }

    private static string Join(string adjective, string noun) => adjective.Length == 0 ? noun : $"{adjective} {noun}";
}
