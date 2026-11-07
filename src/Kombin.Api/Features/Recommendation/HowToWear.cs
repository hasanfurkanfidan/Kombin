using Kombin.Api.Common.Results;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>What every how-to-wear run for one user needs, loaded once (several anchors share it: the social "Dolabımla dene").</summary>
internal sealed record HowToWearContext(
    Guid UserId,
    UserProfile? Profile,
    PersonaChoice Persona,
    List<OutfitPiece> Wardrobe,
    List<Guid> Owned,
    HashSet<string> Dismissed,
    DateTimeOffset Now,
    DateOnly Today);

/// <summary>Exactly one of the two.</summary>
internal sealed record HowToWearAnchor(Guid? ProductId, Guid? WardrobeItemId);

/// <summary>The stored outfits around <c>Anchor</c>; <c>Shopping</c> when they come from the catalog only.</summary>
internal sealed record HowToWearResult(OutfitPiece Anchor, List<Outfit> Outfits, bool Shopping);

/// <summary>
/// The how-to-wear engine around one anchor, shared by <c>GET /me/outfits/how-to-wear</c> and <c>POST /posts/{id}/try</c>: wardrobe companions
/// first, catalog gaps from the narrow prefetch within the persona's budget, catalog-only ideas when the wardrobe cannot help.
/// </summary>
internal static class HowToWear
{
    /// <summary>With an empty (or unhelpful) wardrobe every companion piece may come from the catalog.</summary>
    public const int ShoppingMaxGaps = 3;

    /// <summary>Null when <paramref name="personaId"/> is not one of the user's personas (the catalog convention's 404).</summary>
    public static async Task<HowToWearContext?> LoadAsync(KombinDbContext db, Guid userId, Guid? personaId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        var persona = await OutfitSources.ChoosePersonaAsync(db, userId, personaId, profile?.ActivePersonaId, now, cancellationToken);
        if (persona is null)
        {
            return null;
        }

        return new HowToWearContext(
            userId,
            profile,
            persona,
            await OutfitSources.LoadWardrobeAsync(db, userId, persona.Scoring, cancellationToken),
            await OutfitSources.OwnedProductIdsAsync(db, userId, cancellationToken),
            await OutfitSources.DismissedHashesAsync(db, userId, now, cancellationToken),
            now,
            OutfitSources.Today(now));
    }

    public static async Task<Result<HowToWearResult>> SuggestAsync(
        KombinDbContext db,
        OutfitStore store,
        HowToWearContext context,
        HowToWearAnchor anchor,
        int count,
        int seed,
        Occasion? occasion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(anchor);

        var scoring = context.Persona.Scoring;
        var taste = context.Persona.Taste;

        var found = await FindAnchorAsync(db, context, anchor, scoring, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var piece = found.Value;
        var outfitContext = Context(piece, occasion, taste, context.Today);
        var companions = context.Wardrobe.Where(candidate => candidate.Key != piece.Key).ToList();

        // Companions from the catalog: colours that go with the anchor, formality within reach of it, the persona's budget.
        var colours = ColorRules.PartnersOf(piece.Color).Where(colour => !taste.AvoidedColors.Contains(colour)).ToList();
        var excluded = piece.Source == PieceSource.Catalog ? [.. context.Owned, piece.Id] : context.Owned;
        var catalog = await OutfitSources.LoadCatalogAsync(
            db,
            OutfitSlots.Core
                .Where(slot => slot != piece.Slot)
                .Select(slot => new CatalogQuery(
                    slot,
                    colours,
                    Math.Max(outfitContext.BandMin, piece.Formality - FormalityRules.MaxCoreSpread),
                    Math.Min(outfitContext.BandMax, piece.Formality + FormalityRules.MaxCoreSpread),
                    context.Profile?.Audience ?? Audience.Unisex,
                    taste.BudgetMin,
                    taste.BudgetMax,
                    excluded)),
            scoring,
            cancellationToken);

        var ranked = OutfitGenerator.Generate(new GenerationRequest(companions, catalog, outfitContext, piece, 1, context.Dismissed));
        var picks = OutfitGenerator.PickAround(ranked, piece.Key, seed, count)
            .Select(candidate => OutfitGenerator.WithAccessory(candidate, companions, outfitContext, piece.Key))
            .ToList();

        var shopping = false;
        if (picks.Count == 0)
        {
            ranked = OutfitGenerator.Generate(new GenerationRequest([], catalog, outfitContext, piece, ShoppingMaxGaps, context.Dismissed));
            picks = [.. OutfitGenerator.PickAround(ranked, piece.Key, seed, count)];
            shopping = picks.Count > 0;
        }

        var request = new OutfitRequest(context.UserId, context.Persona.Persona?.Id, OutfitKind.HowToWear, occasion, context.Today, seed, null, outfitContext);
        var outfits = picks.Count == 0
            ? []
            : await store.PersistAsync(request, [.. picks.Select(pick => new OutfitDraft(pick, piece.Key))], context.Now, cancellationToken);

        return new HowToWearResult(piece, outfits, shopping);
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
        KombinDbContext db, HowToWearContext context, HowToWearAnchor anchor, PersonaScoring? scoring, CancellationToken cancellationToken)
    {
        if (anchor.WardrobeItemId is { } itemId)
        {
            return context.Wardrobe.FirstOrDefault(piece => piece.Id == itemId) is { } item
                ? item
                : RecommendationErrors.WardrobeItemNotFound;
        }

        var productId = anchor.ProductId!.Value;
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
            .Where(item => item.UserId == context.UserId && !item.IsArchived && item.ProductId == productId)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (ownedItemId is { } owned && context.Wardrobe.FirstOrDefault(piece => piece.Id == owned) is { } ownedPiece)
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
