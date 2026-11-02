using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Recommendation;

/// <summary>The persona an outfit is built for. <c>Persona</c> is null for a user without personas; the taste is then neutral.</summary>
internal sealed record PersonaChoice(Persona? Persona, PersonaStyleProfile? StyleProfile)
{
    public static readonly PersonaChoice None = new(null, null);

    public PersonaTaste Taste => Persona is null ? PersonaTaste.None : PersonaTaste.From(Persona, StyleProfile!);

    public PersonaScoring? Scoring => Persona is null ? null : new PersonaScoring(Persona, StyleProfile!);

    /// <summary>Colours to look for in shopping ideas: the persona's preferred and learned ones, avoided ones excluded (as in the gap analysis).</summary>
    public IReadOnlyList<ProductColor> PreferredColors => Persona is null ? [] : GapPersona.From(Persona, StyleProfile).PreferredColors;
}

/// <summary>
/// The narrow catalog prefetch for one slot: the slot's categories, the given colours and formality band, active, with a sellable offer
/// whose effective price fits the budget, for the user's audience, newest <see cref="OutfitSources.CatalogPrefetch"/> first.
/// </summary>
internal sealed record CatalogQuery(
    OutfitSlot Slot,
    IReadOnlyCollection<ProductColor> Colors,
    int FormalityMin,
    int FormalityMax,
    Audience Audience,
    decimal? BudgetMin,
    decimal? BudgetMax,
    IReadOnlyCollection<Guid> ExcludedProductIds);

/// <summary>Everything the engine reads from the database: the persona, the wardrobe as pieces, catalog gap candidates, dismissed outfits.</summary>
internal static class OutfitSources
{
    public const int CatalogPrefetch = 30;

    /// <summary>A dismissed combination is not suggested again for this long.</summary>
    public static readonly TimeSpan DismissMemory = TimeSpan.FromDays(30);

    /// <summary>
    /// The catalog's persona convention: an explicit <paramref name="personaId"/> must be the caller's and not archived (else
    /// <c>Profile.PersonaNotFound</c>); without one the active persona is used, else the first, else none.
    /// </summary>
    public static async Task<PersonaChoice?> ChoosePersonaAsync(
        KombinDbContext db, Guid userId, Guid? personaId, Guid? activePersonaId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var personas = await db.Personas.AsNoTracking()
            .OwnedBy(userId)
            .OrderBy(persona => persona.SortOrder)
            .ThenBy(persona => persona.CreatedAt)
            .ToListAsync(cancellationToken);

        Persona? chosen;
        if (personaId is { } requested)
        {
            chosen = personas.FirstOrDefault(persona => persona.Id == requested);
            if (chosen is null)
            {
                return null;
            }
        }
        else
        {
            chosen = personas.FirstOrDefault(persona => persona.Id == activePersonaId) ?? personas.FirstOrDefault();
        }

        return chosen is null
            ? PersonaChoice.None
            : new PersonaChoice(chosen, await StyleProfiles.LoadOrDeriveAsync(db, chosen, now, cancellationToken));
    }

    /// <summary>The user's wearable pieces: ready, not archived, with a category. Accessories included.</summary>
    public static async Task<List<OutfitPiece>> LoadWardrobeAsync(KombinDbContext db, Guid userId, PersonaScoring? scoring, CancellationToken cancellationToken)
    {
        var rows = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && !item.IsArchived && item.Status == WardrobeItemStatus.Ready && item.CategoryId != null)
            .Join(db.Categories, item => item.CategoryId, category => category.Id, (item, category) => new
            {
                item.Id,
                CategorySlug = category.Slug,
                item.PrimaryColor,
                item.Pattern,
                item.Fit,
                item.Formality,
                item.Seasons,
                item.LastWornAt,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => Score(
                new OutfitPiece(
                    PieceSource.Wardrobe,
                    row.Id,
                    OutfitSlots.ForCategorySlug(row.CategorySlug),
                    row.CategorySlug,
                    row.PrimaryColor,
                    row.Pattern,
                    row.Fit,
                    row.Formality,
                    row.Seasons,
                    LastWornAt: row.LastWornAt),
                scoring)),
        ];
    }

    /// <summary>The product IDs the user already owns (catalog items in the wardrobe), never suggested as gaps.</summary>
    public static Task<List<Guid>> OwnedProductIdsAsync(KombinDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && !item.IsArchived && item.ProductId != null)
            .Select(item => item.ProductId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

    public static async Task<HashSet<string>> DismissedHashesAsync(KombinDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - DismissMemory;
        var hashes = await db.Outfits.AsNoTracking()
            .Where(outfit => outfit.UserId == userId && outfit.Status == OutfitStatus.Dismissed && outfit.DismissedAt >= since)
            .Select(outfit => outfit.Hash)
            .ToListAsync(cancellationToken);

        return new HashSet<string>(hashes, StringComparer.Ordinal);
    }

    /// <summary>Runs the queries in order (one DbContext); slots with nothing to look for are skipped.</summary>
    public static async Task<List<OutfitPiece>> LoadCatalogAsync(
        KombinDbContext db, IEnumerable<CatalogQuery> queries, PersonaScoring? scoring, CancellationToken cancellationToken)
    {
        var categories = await db.Categories.AsNoTracking().Select(category => new { category.Id, category.Slug }).ToListAsync(cancellationToken);
        var pieces = new List<OutfitPiece>();

        foreach (var query in queries)
        {
            if (query.Colors.Count == 0 || query.FormalityMin > query.FormalityMax)
            {
                continue;
            }

            var categoryIds = categories.Where(category => OutfitSlots.Fills(category.Slug, query.Slot)).Select(category => category.Id).ToList();
            pieces.AddRange(await LoadSlotAsync(db, query, categoryIds, scoring, cancellationToken));
        }

        return pieces;
    }

    /// <summary>
    /// The cheapest sellable offer of each product by effective price (price plus shipping), ties by price then seller name, as the
    /// product page orders its offers.
    /// </summary>
    public static async Task<Dictionary<Guid, (Guid OfferId, decimal Price)>> CheapestOffersAsync(
        KombinDbContext db, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var offers = await db.Offers.AsNoTracking()
            .Where(offer => productIds.Contains(offer.ProductId)
                && offer.IsActive
                && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock))
            .Select(offer => new { offer.Id, offer.ProductId, offer.CurrentPrice, offer.ShippingCost, offer.FreeShippingThreshold, SellerName = offer.Seller.Name })
            .ToListAsync(cancellationToken);

        return offers
            .GroupBy(offer => offer.ProductId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(offer => (offer.Id, Price: GetProductEndpoint.EffectivePrice(offer.CurrentPrice, offer.ShippingCost, offer.FreeShippingThreshold), offer.CurrentPrice, offer.SellerName))
                    .OrderBy(offer => offer.Price)
                    .ThenBy(offer => offer.CurrentPrice)
                    .ThenBy(offer => offer.SellerName, StringComparer.Ordinal)
                    .Select(offer => (offer.Id, offer.Price))
                    .First());
    }

    public static OutfitPiece Score(OutfitPiece piece, PersonaScoring? scoring) =>
        scoring is null ? piece : piece with { PersonaScore = scoring.Score(piece.Traits) };

    private static async Task<List<OutfitPiece>> LoadSlotAsync(
        KombinDbContext db, CatalogQuery query, List<Guid> categoryIds, PersonaScoring? scoring, CancellationToken cancellationToken)
    {
        var audience = query.Audience;
        var colors = query.Colors.ToList();
        var excluded = query.ExcludedProductIds.ToList();
        var budgetMax = query.BudgetMax;

        var rows = await db.Products.AsNoTracking()
            .Where(product => product.IsActive
                && categoryIds.Contains(product.CategoryId)
                && colors.Contains(product.PrimaryColor)
                && product.Formality >= query.FormalityMin
                && product.Formality <= query.FormalityMax
                && product.Audience != Audience.Kids
                && (audience == Audience.Unisex || product.Audience == Audience.Unisex || product.Audience == audience)
                && !excluded.Contains(product.Id))
            .Where(product => db.Offers.Any(offer => offer.ProductId == product.Id
                && offer.IsActive
                && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock)
                && (budgetMax == null || offer.CurrentPrice <= budgetMax)))
            .OrderByDescending(product => product.CreatedAt)
            .ThenByDescending(product => product.Id)
            .Take(CatalogPrefetch)
            .Select(product => new
            {
                product.Id,
                CategorySlug = product.Category.Slug,
                product.PrimaryColor,
                product.Pattern,
                product.Fit,
                product.Formality,
                product.Seasons,
            })
            .ToListAsync(cancellationToken);

        var offers = await CheapestOffersAsync(db, [.. rows.Select(row => row.Id)], cancellationToken);

        return
        [
            .. rows
                .Where(row => offers.TryGetValue(row.Id, out var offer)
                    && (query.BudgetMin is null || offer.Price >= query.BudgetMin)
                    && (query.BudgetMax is null || offer.Price <= query.BudgetMax))
                .Select(row => Score(
                    new OutfitPiece(
                        PieceSource.Catalog,
                        row.Id,
                        OutfitSlots.ForCategorySlug(row.CategorySlug),
                        row.CategorySlug,
                        row.PrimaryColor,
                        row.Pattern,
                        row.Fit,
                        row.Formality,
                        row.Seasons,
                        OfferId: offers[row.Id].OfferId,
                        Price: offers[row.Id].Price),
                    scoring)),
        ];
    }

    /// <summary>Today in Istanbul, the day the outfit dates are relative to.</summary>
    public static DateOnly Today(DateTimeOffset now) => WearRules.Today(now);
}
