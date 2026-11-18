using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.League.Rules;

/// <summary>
/// An earlier entry of the player. <c>Competed</c>: it was <c>Competing</c> at its day's close (<c>daily_scores.competed</c>), or, while that day is not
/// closed yet, it is <c>Competing</c> now.
/// </summary>
internal sealed record PastEntry(DateOnly Day, IReadOnlyList<Guid> PieceIds, bool Competed);

/// <summary>
/// The repeat rule (rules, section 1; design note section 5). Pure.
/// <list type="bullet">
/// <item>piece set: the sorted identities of the non-accessory pieces (accessories never decide an outfit, as in the engine's hash). A piece's
/// identity is its wardrobe item ID, except for a piece added from the catalog, whose identity is the product ID (<see cref="Identity"/>):
/// archiving it and adding the same product again makes a new wardrobe item but not a new piece;</item>
/// <item>exact: blocked if the same piece set competed on a day in [D − (RepeatExactDays − 1), D − 1] (30 days: "30 gün içinde yalnızca bir kez");</item>
/// <item>near: blocked if a set competed on a day in [D − (RepeatNearDays − 1), D − 1] (7 days) with max(|A \ B|, |B \ A|) ≤ RepeatNearMaxDifference
/// (1): one piece swapped, added or removed.</item>
/// </list>
/// Only competed entries block: withdrawn, removed and not-competing ones do not.
/// </summary>
internal static class RepeatRule
{
    /// <summary>A piece's identity: the product of a piece added from the catalog (<c>FromCatalog</c> with a product), else the wardrobe item.</summary>
    public static Guid Identity(Guid wardrobeItemId, Guid? productId, bool fromCatalog) =>
        fromCatalog && productId is { } product ? product : wardrobeItemId;

    /// <summary>The sorted IDs of the pieces that decide the outfit: everything but accessories, each once.</summary>
    public static Guid[] PieceIds(IEnumerable<(OutfitSlot Slot, Guid Id)> pieces) =>
        [.. pieces.Where(piece => piece.Slot != OutfitSlot.Accessory).Select(piece => piece.Id).Distinct().Order()];

    /// <summary>SHA-256 of the sorted IDs as 32 hex characters (<c>league.entries.piece_set_hash</c>).</summary>
    public static string Hash(IReadOnlyList<Guid> pieceIds) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', pieceIds.Order().Select(id => id.ToString("N"))))))[..32];

    public static bool IsBlocked(IReadOnlyList<Guid> pieceIds, DateOnly day, IEnumerable<PastEntry> earlierEntries, LeagueOptions options)
    {
        ArgumentNullException.ThrowIfNull(pieceIds);
        ArgumentNullException.ThrowIfNull(earlierEntries);
        ArgumentNullException.ThrowIfNull(options);

        var mine = pieceIds.ToHashSet();

        foreach (var earlier in earlierEntries.Where(entry => entry.Competed))
        {
            var daysAgo = day.DayNumber - earlier.Day.DayNumber;
            if (daysAgo < 1)
            {
                continue;
            }

            var theirs = earlier.PieceIds.ToHashSet();
            if (daysAgo <= options.RepeatExactDays - 1 && mine.SetEquals(theirs))
            {
                return true;
            }

            if (daysAgo <= options.RepeatNearDays - 1 && Difference(mine, theirs) <= options.RepeatNearMaxDifference)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>max(|A \ B|, |B \ A|): how many pieces were swapped, added or removed.</summary>
    public static int Difference(IReadOnlySet<Guid> left, IReadOnlySet<Guid> right) =>
        Math.Max(left.Count(id => !right.Contains(id)), right.Count(id => !left.Contains(id)));
}
