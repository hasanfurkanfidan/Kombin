using System.Security.Cryptography;

namespace Kombin.Api.Features.League.Rules;

/// <summary>
/// The per-week alias a group member is shown under ("Yeşil Trençkot"; design note section 4): an everyday colour and a garment, drawn with a
/// cryptographic random number, unique within the group, never the player's alias of the previous week, and never derived from the display
/// name, the e-mail or the user ID (nothing about the person goes in, so nobody can recompute it to link two weeks). 18 × 16 = 288 aliases, so a
/// group of 35 always finds a free one. The lists are pinned by <c>LeagueAliasTests</c> and scanned by <c>CopyGlossaryTests</c>. Pure apart from
/// the default random source.
/// </summary>
internal static class LeagueAliases
{
    public static readonly IReadOnlyList<string> Colours =
    [
        "Kırmızı", "Mavi", "Lacivert", "Yeşil", "Siyah", "Beyaz", "Gri", "Bej", "Kahverengi",
        "Mor", "Pembe", "Turuncu", "Sarı", "Bordo", "Haki", "Krem", "Lila", "Turkuaz",
    ];

    public static readonly IReadOnlyList<string> Garments =
    [
        "Trençkot", "Blazer", "Gömlek", "Kazak", "Hırka", "Mont", "Kaban", "Etek",
        "Pantolon", "Elbise", "Tişört", "Yelek", "Ceket", "Bluz", "Şort", "Atkı",
    ];

    public static int Count => Colours.Count * Garments.Count;

    public static string At(int index) => $"{Colours[index / Garments.Count]} {Garments[index % Garments.Count]}";

    /// <summary>
    /// A free alias: not in <paramref name="taken"/> (the group's) and not <paramref name="previous"/> (the player's last week). <paramref name="next"/>
    /// returns a number in <c>[0, max)</c>; by default <see cref="RandomNumberGenerator.GetInt32(int)"/>. A few random draws, then a walk from a random
    /// start over every alias, so it always ends.
    /// </summary>
    public static string Draw(IReadOnlySet<string> taken, string? previous, Func<int, int>? next = null)
    {
        ArgumentNullException.ThrowIfNull(taken);
        next ??= RandomNumberGenerator.GetInt32;

        bool Free(string alias) => !taken.Contains(alias) && !string.Equals(alias, previous, StringComparison.Ordinal);

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var alias = At(next(Count));
            if (Free(alias))
            {
                return alias;
            }
        }

        var start = next(Count);
        for (var offset = 0; offset < Count; offset++)
        {
            var alias = At((start + offset) % Count);
            if (Free(alias))
            {
                return alias;
            }
        }

        throw new InvalidOperationException($"No free league alias among {Count}.");
    }
}
