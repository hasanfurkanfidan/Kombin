using System.Text;

namespace Kombin.Api.Features.Catalog;

internal static class Slugs
{
    // Each pair is "<letter><replacement>". Explicit transliteration rather than Unicode normalization, so the result
    // does not depend on ICU or invariant-globalization settings. 'I' maps to 'i' because Turkish upper-case ı is I.
    private static readonly Dictionary<char, char> Transliteration = "çc Çc ğg Ğg ıi İi Ii öo Öo şs Şs üu Üu âa Âa îi Îi ûu Ûu ée Ée èe Èe êe Êe ëe Ëe àa Àa áa Áa äa Äa ñn Ñn óo Óo òo Òo ôo Ôo úu Úu ùu Ùu"
        .Split(' ')
        .ToDictionary(pair => pair[0], pair => pair[1]);

    /// <summary>
    /// Lower-case kebab-case ASCII. Turkish letters are transliterated (ç→c, ğ→g, ı→i, ö→o, ş→s, ü→u) and every other
    /// run of non-alphanumeric characters becomes a single hyphen. Returns an empty string when nothing usable remains.
    /// </summary>
    public static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSeparator = false;

        foreach (var character in text)
        {
            var mapped = Transliteration.TryGetValue(character, out var replacement)
                ? replacement
                : char.ToLowerInvariant(character);

            if (mapped is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(mapped);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>Returns <paramref name="slug"/> if free, otherwise the first of <c>slug-2</c>, <c>slug-3</c>, ... that is not in <paramref name="taken"/>.</summary>
    public static string MakeUnique(string slug, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(slug))
        {
            return slug;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{slug}-{suffix}";

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
