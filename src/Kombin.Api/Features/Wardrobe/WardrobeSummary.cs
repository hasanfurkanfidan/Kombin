using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Wardrobe;

/// <summary>Where a piece's price comes from.</summary>
internal enum PriceKind
{
    /// <summary>The user's own <c>purchasePrice</c>: a fact.</summary>
    Own,

    /// <summary>The linked catalog product's lowest price today: an estimate, never to be shown as a fact ("tahmini").</summary>
    Estimated,

    /// <summary>Neither: the piece is counted but left out of every sum.</summary>
    None,
}

/// <summary>Why a piece has not been worn, as far as the engine can tell.</summary>
internal enum UnwornReason
{
    /// <summary>"eşi yok": it pairs with nothing else in the wardrobe (<c>companionCount</c> 0).</summary>
    NoPartner,

    /// <summary>"mevsimi değil": its seasons do not include the current one.</summary>
    OutOfSeason,
}

/// <summary>A ready wardrobe piece as the summary sees it. <c>RootSlug</c> is the top-level category (null without a category).</summary>
internal sealed record SummaryPiece(Guid Id, string? RootSlug, decimal? Price, PriceKind PriceKind, int WearCount, DateOnly? LastWornAt);

internal sealed record ValueCounts(int Priced, int Estimated, int Unpriced);

internal sealed record CategoryValue(string? Slug, string Name, int PieceCount, int UnpricedCount, decimal? Total, decimal EstimatedTotal, bool IsEstimate);

/// <summary>
/// The wardrobe's value. <c>Total</c> is <c>KnownTotal</c> (own prices) plus <c>EstimatedTotal</c> (catalog prices of linked pieces), null when
/// no piece has a price. <c>IsEstimate</c> says the total includes estimates: the app labels it "tahmini". <c>Counts</c> are disjoint and add
/// up to the number of pieces.
/// </summary>
internal sealed record WardrobeValue(
    string Currency,
    decimal? Total,
    decimal KnownTotal,
    decimal EstimatedTotal,
    bool IsEstimate,
    ValueCounts Counts,
    IReadOnlyList<CategoryValue> ByCategory,
    string? Note);

/// <summary>One of the most-worn pieces with what a wear cost: <c>CostPerWear</c> = price / wear count, two decimals.</summary>
internal sealed record CostPerWearEntry(SummaryPiece Piece, decimal CostPerWear);

/// <summary>
/// The rules of the wardrobe summary (design: <c>docs/design/wardrobe-summary-and-size-memory.md</c>). Pure: the endpoint loads, these decide.
/// <list type="bullet">
/// <item>Price: the piece's own price, else the lowest price of its catalog product (an estimate), else none.</item>
/// <item>Cost per wear: pieces with a price and at least one wear, most worn first, <see cref="CostPerWearCount"/> of them.</item>
/// <item>"Giymediklerin": added at least <see cref="UnwornMinAgeDays"/> days ago, no recorded wear, in no saved or worn outfit.</item>
/// <item>The reason, first match: no partner, out of season now, none.</item>
/// </list>
/// </summary>
internal static class WardrobeSummaryRules
{
    /// <summary>Fewer ready pieces than this get a plain message instead of numbers.</summary>
    public const int MinPieces = 5;

    public const int CostPerWearCount = 5;

    public const int UnwornMinAgeDays = 30;

    public const int UnwornListLimit = 20;

    /// <summary>
    /// Catalog suggestions that would unlock a piece are looked up for this many pieces only (each one is a catalog query), so the summary
    /// never costs one query per piece.
    /// </summary>
    public const int UnlockLookups = 3;

    public static (decimal? Price, PriceKind Kind) Resolve(decimal? ownPrice, decimal? catalogLowestPrice) =>
        ownPrice is { } own ? (own, PriceKind.Own)
        : catalogLowestPrice is { } estimate ? (estimate, PriceKind.Estimated)
        : (null, PriceKind.None);

    /// <param name="pieces">Every ready piece.</param>
    /// <param name="categoryNames">The names of the top-level categories by slug; a slug missing here is shown as itself.</param>
    public static WardrobeValue Value(IReadOnlyList<SummaryPiece> pieces, IReadOnlyDictionary<string, string> categoryNames)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(categoryNames);

        var counts = new ValueCounts(
            pieces.Count(piece => piece.PriceKind == PriceKind.Own),
            pieces.Count(piece => piece.PriceKind == PriceKind.Estimated),
            pieces.Count(piece => piece.PriceKind == PriceKind.None));

        var known = pieces.Where(piece => piece.PriceKind == PriceKind.Own).Sum(piece => piece.Price!.Value);
        var estimated = pieces.Where(piece => piece.PriceKind == PriceKind.Estimated).Sum(piece => piece.Price!.Value);
        var anyPrice = counts.Priced + counts.Estimated > 0;

        var byCategory = pieces
            .GroupBy(piece => piece.RootSlug)
            .Select(group =>
            {
                var groupEstimated = group.Where(piece => piece.PriceKind == PriceKind.Estimated).Sum(piece => piece.Price!.Value);
                var priced = group.Where(piece => piece.PriceKind != PriceKind.None).ToList();

                return new CategoryValue(
                    group.Key,
                    group.Key is null ? WardrobeSummaryCopy.UncategorisedName : categoryNames.GetValueOrDefault(group.Key, group.Key),
                    group.Count(),
                    group.Count() - priced.Count,
                    priced.Count == 0 ? null : priced.Sum(piece => piece.Price!.Value),
                    groupEstimated,
                    groupEstimated > 0);
            })
            .OrderByDescending(category => category.Total ?? -1m)
            .ThenBy(category => category.Slug, StringComparer.Ordinal)
            .ToList();

        return new WardrobeValue(
            Offer.DefaultCurrency,
            anyPrice ? known + estimated : null,
            known,
            estimated,
            estimated > 0,
            counts,
            byCategory,
            anyPrice ? null : WardrobeSummaryCopy.NothingPriced);
    }

    public static List<CostPerWearEntry> CostPerWear(IReadOnlyList<SummaryPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        return
        [
            .. pieces
                .Where(piece => piece.PriceKind != PriceKind.None && piece.WearCount > 0)
                .OrderByDescending(piece => piece.WearCount)
                .ThenByDescending(piece => piece.LastWornAt)
                .ThenBy(piece => piece.Id)
                .Take(CostPerWearCount)
                .Select(piece => new CostPerWearEntry(piece, Math.Round(piece.Price!.Value / piece.WearCount, 2, MidpointRounding.AwayFromZero))),
        ];
    }

    /// <summary>Whether a piece belongs in "Giymediklerin" before the outfit check: old enough and never worn.</summary>
    public static bool IsUnwornCandidate(DateTimeOffset createdAt, int wearCount, DateOnly? lastWornAt, DateTimeOffset now) =>
        wearCount == 0 && lastWornAt is null && createdAt <= now.AddDays(-UnwornMinAgeDays);

    public static UnwornReason? ReasonOf(bool hasNoPartner, Season pieceSeasons, Season currentSeason)
    {
        if (hasNoPartner)
        {
            return UnwornReason.NoPartner;
        }

        // A piece with no season set is not "out of season" (nothing was said about it).
        return pieceSeasons != Season.None && (pieceSeasons & currentSeason) == 0 ? UnwornReason.OutOfSeason : null;
    }

    public static string ReasonText(UnwornReason reason) => reason switch
    {
        UnwornReason.NoPartner => WardrobeSummaryCopy.NoPartnerReason,
        UnwornReason.OutOfSeason => WardrobeSummaryCopy.OutOfSeasonReason,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
