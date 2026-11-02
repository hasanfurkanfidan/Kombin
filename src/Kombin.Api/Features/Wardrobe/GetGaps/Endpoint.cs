using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Engagement;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.GetGaps;

/// <summary>
/// <c>PersonaId</c> is the persona the analysis was made for (the active one, else the first), or null for a user without personas.
/// <c>SuggestedProducts</c> fill the first gap: catalog items of its category, color and formality band, active and in stock, not
/// already in the wardrobe, and with a persona a <c>matchScore</c> of at least <see cref="GetGapsEndpoint.MinMatchScore"/>.
/// </summary>
internal sealed record WardrobeGapsResponse(Guid? PersonaId, IReadOnlyList<WardrobeGap> Gaps, IReadOnlyList<ProductListItem> SuggestedProducts);

internal static class GetGapsEndpoint
{
    public const int MinMatchScore = 60;

    public const int MaxSuggestions = 3;

    /// <summary>Only the newest products that fit the gap are scored, as in <c>sort=match</c> of the catalog list.</summary>
    public const int CandidateLimit = 500;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/gaps", Handle)
            .WithName("GetWardrobeGaps")
            .Produces<WardrobeGapsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        var personas = await db.Personas.AsNoTracking()
            .OwnedBy(userId)
            .OrderBy(persona => persona.SortOrder)
            .ThenBy(persona => persona.CreatedAt)
            .ToListAsync(cancellationToken);
        var persona = personas.FirstOrDefault(candidate => candidate.Id == profile?.ActivePersonaId) ?? personas.FirstOrDefault();

        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(category => category.Id, category => category.Slug, cancellationToken);
        var owned = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && !item.IsArchived && item.Status == WardrobeItemStatus.Ready && item.CategoryId != null)
            .Select(item => new { CategoryId = item.CategoryId!.Value, item.PrimaryColor, item.Formality, item.ProductId })
            .ToListAsync(cancellationToken);

        var pieces = owned
            .Select(item => new GapPiece(SizeGroups.ForCategorySlug(categories[item.CategoryId]), item.PrimaryColor, item.Formality))
            .ToList();
        var styleProfile = persona is null ? null : await StyleProfiles.LoadOrDeriveAsync(db, persona, clock.UtcNow, cancellationToken);
        var gaps = GapAnalysis.Analyze(pieces, persona is null ? null : GapPersona.From(persona, styleProfile));

        var suggestions = gaps.Count == 0
            ? []
            : await SuggestAsync(
                db,
                gaps[0],
                persona is null ? null : new PersonaScoring(persona, styleProfile!),
                profile?.Audience ?? Audience.Unisex,
                [.. owned.Select(item => item.ProductId).OfType<Guid>()],
                cancellationToken);

        return TypedResults.Ok(new WardrobeGapsResponse(persona?.Id, gaps, suggestions));
    }

    private static async Task<List<ProductListItem>> SuggestAsync(
        KombinDbContext db, WardrobeGap gap, PersonaScoring? scoring, Audience audience, List<Guid> ownedProductIds, CancellationToken cancellationToken)
    {
        var (minFormality, maxFormality) = GapAnalysis.Range(gap.Band);
        var categoryIds = await db.Categories
            .Where(category => category.Slug == gap.CategorySlug || category.Slug.StartsWith(gap.CategorySlug + "/"))
            .Select(category => category.Id)
            .ToListAsync(cancellationToken);

        var rows = await db.Products.AsNoTracking()
            .Where(product => product.IsActive
                && categoryIds.Contains(product.CategoryId)
                && product.PrimaryColor == gap.Color
                && product.Formality >= minFormality
                && product.Formality <= maxFormality
                && !ownedProductIds.Contains(product.Id))
            .Where(product => product.Offers.Any(offer => offer.IsActive && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock)))
            .OrderByDescending(product => product.CreatedAt)
            .ThenByDescending(product => product.Id)
            .Take(CandidateLimit)
            .Select(product => new
            {
                product.Id,
                product.Audience,
                product.PrimaryColor,
                product.Formality,
                product.Fit,
                product.Seasons,
                product.Pattern,
                CategorySlug = product.Category.Slug,
            })
            .ToListAsync(cancellationToken);

        // Newest first already; with a persona the stable sort keeps that order among equal scores.
        var picks = rows
            .Where(row => WeeklyMatchSelection.AudienceMatches(audience, row.Audience))
            .Select(row => (row.Id, Score: scoring?.Score(new ProductTraits(row.PrimaryColor, row.Formality, row.Fit, row.Seasons, row.Pattern, row.CategorySlug))))
            .Where(pick => pick.Score is null || pick.Score >= MinMatchScore)
            .OrderByDescending(pick => pick.Score ?? 0)
            .Take(MaxSuggestions)
            .ToList();

        var cards = await ProductSummaries.LoadAsync(db, [.. picks.Select(pick => pick.Id)], cancellationToken);

        return [.. picks.Where(pick => cards.ContainsKey(pick.Id)).Select(pick => cards[pick.Id].ToListItem() with { MatchScore = pick.Score })];
    }
}
