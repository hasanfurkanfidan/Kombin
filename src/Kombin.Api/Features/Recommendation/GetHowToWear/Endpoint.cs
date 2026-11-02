using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.GetHowToWear;

/// <summary>Exactly one of <c>ProductId</c> and <c>WardrobeItemId</c>. <c>Count</c> 1-5 (default 5); <c>Seed</c> re-ranks near-equal outfits.</summary>
internal sealed record HowToWearQuery(Guid? ProductId, Guid? WardrobeItemId, int? Count, int? Seed, string? Occasion, Guid? PersonaId);

/// <summary>
/// <c>Outfits</c> are built around the anchor (in every outfit with <c>role</c> Anchor), wardrobe pieces first; gaps come from the catalog
/// within the persona's budget, each with its cheapest offer, and <c>completionPrice</c> sums them. A product the user already owns is
/// anchored as the wardrobe item. <c>Message</c> explains an empty list or catalog-only ideas.
/// </summary>
internal sealed record HowToWearResponse(Guid? ProductId, Guid? WardrobeItemId, IReadOnlyList<OutfitResponse> Outfits, string? Message);

internal static class GetHowToWearEndpoint
{
    public const int DefaultCount = 5;

    public const int MaxCount = 5;

    /// <summary>With an empty (or unhelpful) wardrobe every companion piece may come from the catalog.</summary>
    public const int ShoppingMaxGaps = 3;

    public const string ShoppingMessage = "Dolabında buna uyan parça bulamadık; bunlar katalogdan tamamlama fikirleri.";

    public const string NothingFitsMessage = "Bu parça için şu an uygun bir kombin bulamadık.";

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/how-to-wear", Handle)
            .WithName("GetHowToWear")
            .WithValidation<HowToWearQuery>()
            .Produces<HowToWearResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(
        [AsParameters] HowToWearQuery query,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IObjectStorage storage,
        OutfitStore store,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;
        var today = OutfitSources.Today(now);
        Occasion? occasion = EnumNames.TryParse<Occasion>(query.Occasion, out var parsed) ? parsed : null;
        var seed = query.Seed ?? 0;
        var count = query.Count ?? DefaultCount;

        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        var persona = await OutfitSources.ChoosePersonaAsync(db, userId, query.PersonaId, profile?.ActivePersonaId, now, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        var scoring = persona.Scoring;
        var taste = persona.Taste;
        var wardrobe = await OutfitSources.LoadWardrobeAsync(db, userId, scoring, cancellationToken);
        var owned = await OutfitSources.OwnedProductIdsAsync(db, userId, cancellationToken);

        var anchor = await FindAnchorAsync(db, userId, query, wardrobe, scoring, cancellationToken);
        if (anchor.IsFailure)
        {
            return anchor.Error.ToHttpResult();
        }

        var piece = anchor.Value;
        var context = Context(piece, occasion, taste, today);
        var companions = wardrobe.Where(candidate => candidate.Key != piece.Key).ToList();

        // Companions from the catalog: colours that go with the anchor, formality within reach of it, the persona's budget.
        var colours = ColorRules.PartnersOf(piece.Color).Where(colour => !taste.AvoidedColors.Contains(colour)).ToList();
        var excluded = piece.Source == PieceSource.Catalog ? [.. owned, piece.Id] : owned;
        var catalog = await OutfitSources.LoadCatalogAsync(
            db,
            OutfitSlots.Core
                .Where(slot => slot != piece.Slot)
                .Select(slot => new CatalogQuery(
                    slot,
                    colours,
                    Math.Max(context.BandMin, piece.Formality - FormalityRules.MaxCoreSpread),
                    Math.Min(context.BandMax, piece.Formality + FormalityRules.MaxCoreSpread),
                    profile?.Audience ?? Audience.Unisex,
                    taste.BudgetMin,
                    taste.BudgetMax,
                    excluded)),
            scoring,
            cancellationToken);

        var dismissed = await OutfitSources.DismissedHashesAsync(db, userId, now, cancellationToken);
        var ranked = OutfitGenerator.Generate(new GenerationRequest(companions, catalog, context, piece, 1, dismissed));
        var picks = OutfitGenerator.PickAround(ranked, piece.Key, seed, count)
            .Select(candidate => OutfitGenerator.WithAccessory(candidate, companions, context, piece.Key))
            .ToList();

        var shopping = false;
        if (picks.Count == 0)
        {
            ranked = OutfitGenerator.Generate(new GenerationRequest([], catalog, context, piece, ShoppingMaxGaps, dismissed));
            picks = [.. OutfitGenerator.PickAround(ranked, piece.Key, seed, count)];
            shopping = picks.Count > 0;
        }

        var request = new OutfitRequest(userId, persona.Persona?.Id, OutfitKind.HowToWear, occasion, today, seed, null, context);
        var outfits = picks.Count == 0 ? [] : await store.PersistAsync(request, [.. picks.Select(pick => new OutfitDraft(pick, piece.Key))], now, cancellationToken);

        return TypedResults.Ok(new HowToWearResponse(
            piece.Source == PieceSource.Catalog ? piece.Id : null,
            piece.Source == PieceSource.Wardrobe ? piece.Id : null,
            await OutfitResponses.BuildAsync(db, storage, outfits, cancellationToken),
            shopping ? ShoppingMessage : picks.Count == 0 ? NothingFitsMessage : null));
    }

    /// <summary>
    /// The day does not matter here (neutral weather); the occasion's band does when one is given, otherwise the band is the anchor's
    /// formality ± 1, aiming at the anchor itself, with the persona's family weights and avoided colours.
    /// </summary>
    private static OutfitContext Context(OutfitPiece anchor, Occasion? occasion, PersonaTaste taste, DateOnly today)
    {
        if (occasion is not null)
        {
            return OutfitContext.Create(occasion, taste, WeatherRules.Neutral, today);
        }

        return new OutfitContext(
            Math.Max(1, anchor.Formality - FormalityRules.MaxCoreSpread),
            Math.Min(5, anchor.Formality + FormalityRules.MaxCoreSpread),
            anchor.Formality,
            null,
            taste.Families,
            WeatherRules.Neutral,
            today,
            taste.AvoidedColors,
            null);
    }

    private static async Task<Result<OutfitPiece>> FindAnchorAsync(
        KombinDbContext db, Guid userId, HowToWearQuery query, List<OutfitPiece> wardrobe, PersonaScoring? scoring, CancellationToken cancellationToken)
    {
        if (query.WardrobeItemId is { } itemId)
        {
            return wardrobe.FirstOrDefault(piece => piece.Id == itemId) is { } item
                ? item
                : RecommendationErrors.WardrobeItemNotFound;
        }

        var productId = query.ProductId!.Value;
        var product = await db.Products.AsNoTracking()
            .Where(candidate => candidate.Id == productId)
            .Select(candidate => new
            {
                candidate.Id,
                CategorySlug = candidate.Category.Slug,
                candidate.PrimaryColor,
                candidate.Pattern,
                candidate.Fit,
                candidate.Formality,
                candidate.Seasons,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            return RecommendationErrors.ProductNotFound;
        }

        // Already in the wardrobe: anchor the user's own piece, so the outfits can be complete.
        var ownedItemId = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && !item.IsArchived && item.ProductId == productId)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (ownedItemId is { } owned && wardrobe.FirstOrDefault(piece => piece.Id == owned) is { } ownedPiece)
        {
            return ownedPiece;
        }

        var offers = await OutfitSources.CheapestOffersAsync(db, [productId], cancellationToken);
        var offer = offers.TryGetValue(productId, out var cheapest) ? cheapest : ((Guid OfferId, decimal Price)?)null;

        return OutfitSources.Score(
            new OutfitPiece(
                PieceSource.Catalog,
                product.Id,
                OutfitSlots.ForCategorySlug(product.CategorySlug),
                product.CategorySlug,
                product.PrimaryColor,
                product.Pattern,
                product.Fit,
                product.Formality,
                product.Seasons,
                OfferId: offer?.OfferId,
                Price: offer?.Price),
            scoring);
    }
}
