using System.Text.Json.Serialization;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Wardrobe.GetGaps;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Wardrobe.GetPairings;

/// <summary><c>Limit</c> is the most companions per slot, 1-20 (default 6).</summary>
internal sealed record PairingsQuery(Guid? PersonaId, int? Limit);

/// <summary>A piece that goes with the item. <c>Score</c> is 0-100 (see <c>OutfitPairs</c>), <c>Reason</c> names the colour harmony.</summary>
internal sealed record PairingCompanion(WardrobeItemSummary Item, int Score, PairReason Reason);

/// <summary>A slot other than the item's own, possibly empty. <c>Total</c> counts every companion in it, also those past the limit.</summary>
internal sealed record PairingSlot(OutfitSlot Slot, int Total, IReadOnlyList<PairingCompanion> Items);

/// <summary>
/// The catalog piece that would help the item most. <c>LowestPrice</c>, <c>CheapestSeller</c> and <c>GoPath</c> are one offer, the cheapest
/// by effective price. <c>UnlocksEstimate</c> is the outfits the item would gain. <c>MatchScore</c> is present only with a persona.
/// </summary>
internal sealed record PairingUnlock(
    OutfitSlot Slot,
    Guid ProductId,
    string Name,
    string CategorySlug,
    ProductColor Color,
    string? ImageUrl,
    decimal LowestPrice,
    SellerRef CheapestSeller,
    string GoPath,
    int UnlocksEstimate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MatchScore = null);

/// <summary>
/// What goes with one wardrobe item. <c>PersonaId</c> is the persona the scores are weighted for, or null for a user without personas.
/// <c>OutfitCount</c> is how many of the outfits behind the stats' <c>possibleOutfits</c> wear the item; a layer or an accessory counts the
/// outfits it can be worn with. <c>Unlock</c> is null when every slot has enough companions, or the catalog has nothing that fits.
/// </summary>
internal sealed record PairingsResponse(Guid ItemId, Guid? PersonaId, IReadOnlyList<PairingSlot> Companions, int OutfitCount, PairingUnlock? Unlock);

internal static class GetPairingsEndpoint
{
    public const int DefaultLimit = 6;

    public const int MaxLimit = 20;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/items/{id:guid}/pairings", Handle)
            .WithName("GetWardrobeItemPairings")
            .WithValidation<PairingsQuery>()
            .Produces<PairingsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>The item must be wearable, like a how-to-wear anchor: an archived, unfinished or uncategorised item has no pairings.</summary>
    private static async Task<IResult> Handle(
        Guid id,
        [AsParameters] PairingsQuery query,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IObjectStorage storage,
        IMemoryCache cache,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        var persona = await OutfitSources.ChoosePersonaAsync(db, userId, query.PersonaId, profile?.ActivePersonaId, clock.UtcNow, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        var scoring = persona.Scoring;
        var wardrobe = await OutfitSources.LoadWardrobeAsync(db, userId, scoring, cancellationToken);
        if (wardrobe.FirstOrDefault(piece => piece.Id == id) is not { } item)
        {
            return RecommendationErrors.WardrobeItemNotFound.ToHttpResult();
        }

        var groups = OutfitPairs.Companions(item, wardrobe, query.Limit ?? DefaultLimit, weighPersona: scoring is not null);

        var shownIds = groups.SelectMany(group => group.Companions).Select(companion => companion.Piece.Id).ToList();
        var shownItems = await db.WardrobeItems.AsNoTracking().Where(candidate => shownIds.Contains(candidate.Id)).ToListAsync(cancellationToken);
        var summaries = (await WardrobeItemResponses.SummarizeAsync(db, storage, shownItems, cancellationToken)).ToDictionary(summary => summary.Id);

        var unlock = await FindUnlockAsync(db, userId, item, wardrobe, groups, persona, profile?.Audience ?? Audience.Unisex, cancellationToken);

        return TypedResults.Ok(new PairingsResponse(
            id,
            persona.Persona?.Id,
            [
                .. groups.Select(group => new PairingSlot(
                    group.Slot,
                    group.Total,
                    [.. group.Companions.Select(companion => new PairingCompanion(summaries[companion.Piece.Id], companion.Score, companion.Reason))])),
            ],
            await OutfitCounts.ItemOutfitsAsync(db, cache, userId, item, wardrobe, cancellationToken),
            unlock));
    }

    /// <summary>Goes through the slots emptiest first and stops at the first with something to suggest; usually that is one catalog query.</summary>
    private static async Task<PairingUnlock?> FindUnlockAsync(
        KombinDbContext db,
        Guid userId,
        OutfitPiece item,
        List<OutfitPiece> wardrobe,
        List<PairGroup> groups,
        PersonaChoice persona,
        Audience audience,
        CancellationToken cancellationToken)
    {
        var slots = OutfitPairs.UnlockSlots(item, groups);
        if (slots.Count == 0)
        {
            return null;
        }

        var scoring = persona.Scoring;
        var taste = persona.Taste;
        var owned = await OutfitSources.OwnedProductIdsAsync(db, userId, cancellationToken);
        var colours = ColorRules.PartnersOf(item.Color).Where(colour => !taste.AvoidedColors.Contains(colour)).ToList();

        foreach (var slot in slots)
        {
            var query = new CatalogQuery(
                slot,
                colours,
                Math.Max(1, item.Formality - FormalityRules.MaxCoreSpread),
                Math.Min(5, item.Formality + FormalityRules.MaxCoreSpread),
                audience,
                taste.BudgetMin,
                taste.BudgetMax,
                owned);

            var catalog = await OutfitSources.LoadCatalogAsync(db, [query], scoring, cancellationToken);
            var pick = OutfitPairs.PickUnlock(item, wardrobe, catalog, scoring is null ? null : GetGapsEndpoint.MinMatchScore);

            if (pick is not null)
            {
                return await DescribeAsync(db, pick, scoring is not null, cancellationToken);
            }
        }

        return null;
    }

    private static async Task<PairingUnlock> DescribeAsync(KombinDbContext db, PairUnlock unlock, bool hasPersona, CancellationToken cancellationToken)
    {
        var piece = unlock.Piece;
        var offerId = piece.OfferId!.Value;

        var offer = await db.Offers.AsNoTracking()
            .Where(candidate => candidate.Id == offerId)
            .Select(candidate => new
            {
                ProductName = candidate.Product.Name,
                BrandName = candidate.Product.Brand.Name,
                candidate.Product.PrimaryImageUrl,
                SellerName = candidate.Seller.Name,
                SellerSlug = candidate.Seller.Slug,
            })
            .SingleAsync(cancellationToken);

        return new PairingUnlock(
            piece.Slot,
            piece.Id,
            Copy.ProductDisplayName(offer.BrandName, offer.ProductName),
            piece.CategorySlug,
            piece.Color,
            offer.PrimaryImageUrl,
            piece.Price!.Value,
            new SellerRef(offer.SellerName, offer.SellerSlug),
            $"/api/v1/go/{offerId}",
            unlock.UnlocksEstimate,
            hasPersona ? piece.PersonaScore : null);
    }
}
