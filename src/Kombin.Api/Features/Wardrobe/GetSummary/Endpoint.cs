using System.Text.Json.Serialization;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Wardrobe.GetPairings;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Wardrobe.GetSummary;

/// <summary>A most-worn piece with its price. <c>PriceIsEstimate</c> is true when the price is the catalog's (the app says "tahmini").</summary>
internal sealed record CostPerWearItem(WardrobeItemSummary Item, decimal Price, bool PriceIsEstimate, int WearCount, decimal CostPerWear);

/// <summary>A piece of "Giymediklerin". <c>Reason</c> is the likely one (null when none); <c>Unlock</c> is the pairings route's catalog suggestion, present for the first few "eşi yok" pieces only.</summary>
internal sealed record UnwornItem(WardrobeItemSummary Item, int DaysInWardrobe, UnwornReason? Reason, string? ReasonText, PairingUnlock? Unlock);

/// <summary><c>TotalCount</c> counts every such piece, <c>Items</c> are the oldest <see cref="WardrobeSummaryRules.UnwornListLimit"/> of them.</summary>
internal sealed record UnwornSection(int TotalCount, IReadOnlyList<UnwornItem> Items, string? Message);

/// <summary>
/// The wardrobe summary. With too few pieces only <c>PieceCount</c> and <c>Message</c> are there: no totals, no zeros that look like data.
/// Otherwise <c>Value</c>, <c>CostPerWear</c> and <c>Unworn</c> are (see <see cref="WardrobeSummaryRules"/>). Every price that is not the
/// user's own is marked: <c>Value.IsEstimate</c>, <c>CategoryValue.IsEstimate</c>, <c>CostPerWearItem.PriceIsEstimate</c>.
/// </summary>
internal sealed record WardrobeSummaryResponse(
    int PieceCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WardrobeValue? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CostPerWearItem>? CostPerWear,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UnwornSection? Unworn);

internal static class GetSummaryEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/summary", Handle)
            .WithName("GetWardrobeSummary")
            .Produces<WardrobeSummaryResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(
        ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IClock clock, IMemoryCache cache, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        var rows = await db.WardrobeItems.AsNoTracking()
            .VisibleOf(userId)
            .Where(item => !item.IsArchived && item.Status == WardrobeItemStatus.Ready)
            .Select(item => new { item.Id, item.CategoryId, item.ProductId, item.PurchasePrice, item.WearCount, item.LastWornAt, item.CreatedAt, item.Seasons })
            .ToListAsync(cancellationToken);

        if (rows.Count < WardrobeSummaryRules.MinPieces)
        {
            return TypedResults.Ok(new WardrobeSummaryResponse(
                rows.Count,
                rows.Count == 0 ? WardrobeSummaryCopy.EmptyWardrobe : WardrobeSummaryCopy.FewPieces(rows.Count, WardrobeSummaryRules.MinPieces),
                null,
                null,
                null));
        }

        // The category table is the taxonomy (a few dozen rows): one read gives every slug and the names of the roots.
        var categories = await db.Categories.AsNoTracking().Select(category => new { category.Id, category.Slug, category.Name }).ToListAsync(cancellationToken);
        var slugById = categories.ToDictionary(category => category.Id, category => category.Slug);
        var rootNames = categories.Where(category => !category.Slug.Contains('/', StringComparison.Ordinal)).ToDictionary(category => category.Slug, category => category.Name);

        var lowestPrices = await LowestPricesAsync(db, [.. rows.Where(row => row.PurchasePrice is null && row.ProductId is not null).Select(row => row.ProductId!.Value).Distinct()], cancellationToken);

        var pieces = rows
            .Select(row =>
            {
                // A product nobody sells is missing from the dictionary: that is "no price", not a price of 0.
                var catalogPrice = row.ProductId is { } productId && lowestPrices.TryGetValue(productId, out var lowest) ? lowest : (decimal?)null;
                var (price, kind) = WardrobeSummaryRules.Resolve(row.PurchasePrice, catalogPrice);
                var root = row.CategoryId is { } categoryId && slugById.TryGetValue(categoryId, out var slug) ? slug.Split('/')[0] : null;

                return new SummaryPiece(row.Id, root, price, kind, row.WearCount, row.LastWornAt);
            })
            .ToList();

        var costPerWear = WardrobeSummaryRules.CostPerWear(pieces);
        var unworn = await UnwornAsync(db, cache, userId, now, [.. rows.Select(row => (row.Id, row.CreatedAt, row.WearCount, row.LastWornAt, row.Seasons))], cancellationToken);

        // One read for every item that is shown, one summary build for all of them.
        var shownIds = costPerWear.Select(entry => entry.Piece.Id).Concat(unworn.Shown.Select(item => item.Id)).ToList();
        var shownItems = await db.WardrobeItems.AsNoTracking().Where(item => shownIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var summaries = (await WardrobeItemResponses.SummarizeAsync(db, storage, shownItems, cancellationToken)).ToDictionary(summary => summary.Id);

        var unlocks = await UnlocksAsync(db, userId, now, unworn.Shown.Where(item => item.Reason == UnwornReason.NoPartner).Select(item => item.Id).Take(WardrobeSummaryRules.UnlockLookups).ToList(), cancellationToken);

        return TypedResults.Ok(new WardrobeSummaryResponse(
            rows.Count,
            null,
            WardrobeSummaryRules.Value(pieces, rootNames),
            [
                .. costPerWear.Select(entry => new CostPerWearItem(
                    summaries[entry.Piece.Id],
                    entry.Piece.Price!.Value,
                    entry.Piece.PriceKind == PriceKind.Estimated,
                    entry.Piece.WearCount,
                    entry.CostPerWear)),
            ],
            new UnwornSection(
                unworn.TotalCount,
                [
                    .. unworn.Shown.Select(item => new UnwornItem(
                        summaries[item.Id],
                        (int)Math.Floor((now - item.CreatedAt).TotalDays),
                        item.Reason,
                        item.Reason is { } reason ? WardrobeSummaryRules.ReasonText(reason) : null,
                        unlocks.GetValueOrDefault(item.Id))),
                ],
                unworn.TotalCount == 0 ? WardrobeSummaryCopy.NothingUnworn : null)));
    }

    /// <summary>The catalog's lowest price (lowest active, in-stock offer, as in the product list) of each of the products, in one query. A product nobody sells is missing.</summary>
    private static async Task<Dictionary<Guid, decimal>> LowestPricesAsync(KombinDbContext db, List<Guid> productIds, CancellationToken cancellationToken) =>
        productIds.Count == 0
            ? []
            : await db.Offers.AsNoTracking()
                .Where(offer => productIds.Contains(offer.ProductId) && offer.IsActive && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock))
                .GroupBy(offer => offer.ProductId)
                .Select(group => new { ProductId = group.Key, Lowest = group.Min(offer => offer.CurrentPrice) })
                .ToDictionaryAsync(row => row.ProductId, row => row.Lowest, cancellationToken);

    private sealed record UnwornPick(Guid Id, DateTimeOffset CreatedAt, UnwornReason? Reason);

    private sealed record UnwornResult(int TotalCount, List<UnwornPick> Shown);

    /// <summary>
    /// "Giymediklerin": old enough, never worn, in no saved or worn outfit (one query for the candidates), oldest first. The reason comes from the
    /// engine's companion counts (the same number as the grid) and the current season.
    /// </summary>
    private static async Task<UnwornResult> UnwornAsync(
        KombinDbContext db,
        IMemoryCache cache,
        Guid userId,
        DateTimeOffset now,
        List<(Guid Id, DateTimeOffset CreatedAt, int WearCount, DateOnly? LastWornAt, Season Seasons)> rows,
        CancellationToken cancellationToken)
    {
        var candidates = rows.Where(row => WardrobeSummaryRules.IsUnwornCandidate(row.CreatedAt, row.WearCount, row.LastWornAt, now)).ToList();
        if (candidates.Count == 0)
        {
            return new UnwornResult(0, []);
        }

        var candidateIds = candidates.Select(row => row.Id).ToList();
        var inOutfits = (await (
                from outfitItem in db.OutfitItems.AsNoTracking()
                join outfit in db.Outfits.AsNoTracking() on outfitItem.OutfitId equals outfit.Id
                where outfit.UserId == userId
                    && (outfit.Status == OutfitStatus.Saved || outfit.Status == OutfitStatus.Worn)
                    && outfitItem.WardrobeItemId != null
                    && candidateIds.Contains(outfitItem.WardrobeItemId.Value)
                select outfitItem.WardrobeItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var unworn = candidates
            .Where(row => !inOutfits.Contains(row.Id))
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .ToList();
        if (unworn.Count == 0)
        {
            return new UnwornResult(0, []);
        }

        var companions = await OutfitCounts.CompanionCountsAsync(db, cache, userId, cancellationToken);
        var season = WeatherRules.SeasonOf(WearRules.Today(now));

        return new UnwornResult(
            unworn.Count,
            [
                .. unworn.Take(WardrobeSummaryRules.UnwornListLimit).Select(row => new UnwornPick(
                    row.Id,
                    row.CreatedAt,
                    WardrobeSummaryRules.ReasonOf(companions.TryGetValue(row.Id, out var count) && count == 0, row.Seasons, season))),
            ]);
    }

    /// <summary>
    /// The pairings route's own suggestion (<see cref="GetPairingsEndpoint.FindUnlockAsync"/>) for the given pieces: at most
    /// <see cref="WardrobeSummaryRules.UnlockLookups"/> of them, each one catalog query. Nothing is loaded when there are none.
    /// </summary>
    private static async Task<Dictionary<Guid, PairingUnlock>> UnlocksAsync(
        KombinDbContext db, Guid userId, DateTimeOffset now, List<Guid> pieceIds, CancellationToken cancellationToken)
    {
        if (pieceIds.Count == 0)
        {
            return [];
        }

        var profile = await db.UserProfiles.AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Select(candidate => new { candidate.ActivePersonaId, candidate.Audience })
            .SingleOrDefaultAsync(cancellationToken);
        var persona = await OutfitSources.ChoosePersonaAsync(db, userId, null, profile?.ActivePersonaId, now, cancellationToken) ?? PersonaChoice.None;
        var scoring = persona.Scoring;
        var wardrobe = await OutfitSources.LoadWardrobeAsync(db, userId, scoring, cancellationToken);

        var unlocks = new Dictionary<Guid, PairingUnlock>();
        foreach (var pieceId in pieceIds)
        {
            if (wardrobe.FirstOrDefault(piece => piece.Id == pieceId) is not { } piece)
            {
                continue;
            }

            var groups = OutfitPairs.Companions(piece, wardrobe, GetPairingsEndpoint.DefaultLimit, weighPersona: scoring is not null);
            if (await GetPairingsEndpoint.FindUnlockAsync(db, userId, piece, wardrobe, groups, persona, profile?.Audience ?? Audience.Unisex, cancellationToken) is { } unlock)
            {
                unlocks[pieceId] = unlock;
            }
        }

        return unlocks;
    }
}
