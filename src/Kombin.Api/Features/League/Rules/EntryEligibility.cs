using Kombin.Api.Common.Time;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.League.Rules;

/// <summary>
/// One piece of the worn outfit as the eligibility check sees it. <c>WardrobeItemId</c> null: a catalog piece (a gap, or a catalog anchor) the
/// player does not own. <c>Found</c>: the wardrobe item exists and is the player's. <c>HasImage</c>: there is something to show (a stored
/// thumbnail, or the product image of a catalog item).
/// </summary>
internal sealed record EligibilityPiece(OutfitSlot Slot, Guid? WardrobeItemId, bool Found, bool Archived, ModerationVerdict? Verdict, bool HasImage);

/// <summary>Whether the day's worn outfit competes, and if not, why (the owner sees the reason).</summary>
internal sealed record EntryVerdict(LeagueEntryStatus Status, LeagueEntryReason? Reason)
{
    public static readonly EntryVerdict Competing = new(LeagueEntryStatus.Competing, null);

    public static readonly EntryVerdict Pending = new(LeagueEntryStatus.Pending, null);

    public static EntryVerdict Not(LeagueEntryReason reason) => new(LeagueEntryStatus.NotCompeting, reason);
}

/// <summary>
/// The eligibility of a worn outfit (design note section 5), in order; the first failure is the reason. Pure.
/// <list type="number">
/// <item>age: a birth year that keeps the player out (<see cref="AgeRule.IsUnderAge"/>) → <c>AgeRestricted</c>;</item>
/// <item>every piece a wardrobe piece and at least <c>League:MinPieces</c> non-accessory pieces → else <c>IncompleteOutfit</c>;</item>
/// <item>every piece still there, not archived, with an image → else <c>PieceUnavailable</c>;</item>
/// <item>the repeat rule → <c>RecentlyCompeted</c>;</item>
/// <item>safety: any piece reviewed or blocked → <c>UnsafePiece</c> (the owner is not told which); any piece without a verdict yet → <c>Pending</c>;</item>
/// <item>otherwise competing.</item>
/// </list>
/// </summary>
internal static class EntryEligibility
{
    public static EntryVerdict Evaluate(bool underAge, IReadOnlyList<EligibilityPiece> pieces, int minPieces, bool recentlyCompeted)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        if (underAge)
        {
            return EntryVerdict.Not(LeagueEntryReason.AgeRestricted);
        }

        if (pieces.Any(piece => piece.WardrobeItemId is null) || pieces.Count(piece => piece.Slot != OutfitSlot.Accessory) < minPieces)
        {
            return EntryVerdict.Not(LeagueEntryReason.IncompleteOutfit);
        }

        if (pieces.Any(piece => !piece.Found || piece.Archived || !piece.HasImage))
        {
            return EntryVerdict.Not(LeagueEntryReason.PieceUnavailable);
        }

        if (recentlyCompeted)
        {
            return EntryVerdict.Not(LeagueEntryReason.RecentlyCompeted);
        }

        if (pieces.Any(piece => piece.Verdict is ModerationVerdict.Review or ModerationVerdict.Block))
        {
            return EntryVerdict.Not(LeagueEntryReason.UnsafePiece);
        }

        return pieces.Any(piece => piece.Verdict is null) ? EntryVerdict.Pending : EntryVerdict.Competing;
    }
}
