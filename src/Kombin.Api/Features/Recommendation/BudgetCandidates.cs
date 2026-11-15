using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>One slot's candidate query: the slot's categories, the formality band, the audience, no avoided colour, not owned, priced at most <c>Cap</c>.</summary>
internal sealed record BudgetQuery(
    OutfitSlot Slot,
    int FormalityMin,
    int FormalityMax,
    Audience Audience,
    IReadOnlyCollection<ProductColor> AvoidedColors,
    decimal Cap,
    IReadOnlyCollection<Guid> ExcludedProductIds);

/// <summary>The catalog pieces a budget search may buy, each with the offer it would buy (in the caller's size when known).</summary>
internal sealed record BudgetCatalog(List<OutfitPiece> Pieces, Dictionary<Guid, BudgetOffer> Offers);

/// <summary>
/// The budget search's catalog side: per slot, one indexed query picks at most <see cref="PerSlot"/> products (newest <see cref="PerBand"/> in each
/// of <see cref="Bands"/> equal price bands up to the cap), then the offers of those products are read once and the one to buy is chosen in the
/// caller's size (<see cref="BudgetOffers.Choose"/>).
/// </summary>
internal static class BudgetCandidates
{
    public const int Bands = 3;

    public const int PerBand = 100;

    public const int PerSlot = Bands * PerBand;

    private sealed record CandidateRow(Guid Id);

    public static async Task<BudgetCatalog> LoadAsync(
        KombinDbContext db, IReadOnlyList<BudgetQuery> queries, Guid? userId, PersonaScoring? scoring, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);

        var categories = await db.Categories.AsNoTracking().Select(category => new { category.Id, category.Slug }).ToListAsync(cancellationToken);
        var ids = new List<Guid>();
        var caps = new Dictionary<Guid, decimal>();

        foreach (var query in queries)
        {
            var categoryIds = categories.Where(category => OutfitSlots.Fills(category.Slug, query.Slot)).Select(category => category.Id).ToArray();
            if (categoryIds.Length == 0 || query.FormalityMin > query.FormalityMax || query.Cap <= 0)
            {
                continue;
            }

            foreach (var row in await SlotAsync(db, query, categoryIds, cancellationToken))
            {
                ids.Add(row.Id);
                caps[row.Id] = query.Cap;
            }
        }

        if (ids.Count == 0)
        {
            return new BudgetCatalog([], []);
        }

        var products = await db.Products.AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .Select(product => new
            {
                product.Id,
                product.BrandId,
                CategorySlug = product.Category.Slug,
                product.PrimaryColor,
                product.Pattern,
                product.Fit,
                product.Formality,
                product.Seasons,
            })
            .ToListAsync(cancellationToken);

        var offers = (await db.Offers.AsNoTracking()
                .Where(offer => ids.Contains(offer.ProductId)
                    && offer.IsActive
                    && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock))
                .Select(offer => new
                {
                    offer.Id,
                    offer.ProductId,
                    offer.CurrentPrice,
                    offer.OriginalPrice,
                    offer.ShippingCost,
                    offer.FreeShippingThreshold,
                    offer.AvailableSizes,
                    SellerName = offer.Seller.Name,
                })
                .ToListAsync(cancellationToken))
            .ToLookup(offer => offer.ProductId);

        var sizes = userId is { } user
            ? (await db.SizeEntries.AsNoTracking()
                    .Where(entry => entry.UserId == user)
                    .Select(entry => new { entry.Group, entry.BrandId, entry.Size, entry.UpdatedAt })
                    .ToListAsync(cancellationToken))
                .ToLookup(entry => entry.Group, entry => (entry.BrandId, entry.Size, entry.UpdatedAt))
            : null;

        var pieces = new List<OutfitPiece>(products.Count);
        var chosen = new Dictionary<Guid, BudgetOffer>(products.Count);

        foreach (var product in products.OrderBy(product => product.Id))
        {
            var group = SizeGroups.ForCategorySlug(product.CategorySlug);
            var size = sizes is null ? null : BudgetOffers.SizeFor([.. sizes[group]], product.BrandId);
            var options = offers[product.Id]
                .Select(offer => new OfferOption(
                    offer.Id,
                    GetProductEndpoint.EffectivePrice(offer.CurrentPrice, offer.ShippingCost, offer.FreeShippingThreshold),
                    offer.CurrentPrice,
                    offer.OriginalPrice,
                    offer.SellerName,
                    offer.AvailableSizes))
                .ToList();

            // The size-specific offer may cost more than the cheapest one the query saw: the cap is checked again.
            if (BudgetOffers.Choose(options, size) is not { } offer || offer.Price > caps[product.Id])
            {
                continue;
            }

            chosen[product.Id] = offer;
            pieces.Add(OutfitSources.Score(
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
                    OfferId: offer.OfferId,
                    Price: offer.Price),
                scoring));
        }

        return new BudgetCatalog(pieces, chosen);
    }

    /// <summary>
    /// The slot's candidates. The cheapest sellable offer of a product (effective price) decides its price band; the newest
    /// <see cref="PerBand"/> of each band are kept. Uses <c>ix_products_budget</c> (category, audience, formality, active) and
    /// <c>ix_offers_sellable_price</c> (product, price, active and in stock).
    /// </summary>
    private static Task<List<CandidateRow>> SlotAsync(KombinDbContext db, BudgetQuery query, Guid[] categoryIds, CancellationToken cancellationToken)
    {
        var avoided = query.AvoidedColors.Select(colour => colour.ToString()).ToArray();
        var excluded = query.ExcludedProductIds.ToArray();
        var audience = query.Audience.ToString();

        return db.Database.SqlQuery<CandidateRow>($"""
            WITH priced AS (
                SELECT p.id, p.created_at, cheapest.price
                FROM catalog.products p
                CROSS JOIN LATERAL (
                    SELECT MIN(o.current_price + CASE WHEN o.free_shipping_threshold IS NOT NULL AND o.current_price >= o.free_shipping_threshold
                                                      THEN 0 ELSE COALESCE(o.shipping_cost, 0) END) AS price
                    FROM catalog.offers o
                    WHERE o.product_id = p.id AND o.is_active AND o.stock_status IN ('InStock', 'LowStock')
                ) cheapest
                WHERE p.is_active
                  AND p.category_id = ANY({categoryIds})
                  AND p.formality BETWEEN {query.FormalityMin} AND {query.FormalityMax}
                  AND p.audience <> 'Kids'
                  AND ({audience} = 'Unisex' OR p.audience = 'Unisex' OR p.audience = {audience})
                  AND NOT (p.primary_color = ANY({avoided}))
                  AND NOT (p.id = ANY({excluded}))
                  AND cheapest.price IS NOT NULL
                  AND cheapest.price <= {query.Cap}
            ),
            ranked AS (
                SELECT id, ROW_NUMBER() OVER (
                    PARTITION BY LEAST({Bands - 1}, FLOOR(price * {Bands} / {query.Cap})::int)
                    ORDER BY created_at DESC, id DESC) AS position
                FROM priced
            )
            SELECT id FROM ranked WHERE position <= {PerBand}
            """).ToListAsync(cancellationToken);
    }
}
