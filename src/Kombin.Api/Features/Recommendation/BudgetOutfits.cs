using System.Text.Json.Serialization;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>What the caller asked for, validated. <c>Date</c> is resolved (today by default).</summary>
internal sealed record BudgetAsk(decimal Budget, Occasion? Occasion, DateOnly Date, bool UseWardrobe, int Seed, int Count);

/// <summary>Who is asking: a signed-in user (wardrobe, persona, sizes, weather of their city) or an anonymous visitor (none of it).</summary>
internal sealed record BudgetCaller(
    Guid? UserId,
    Audience Audience,
    PersonaChoice Persona,
    IReadOnlyList<OutfitPiece> Wardrobe,
    IReadOnlyCollection<Guid> Owned,
    IReadOnlySet<string> Dismissed,
    WeatherSnapshot? Weather)
{
    public static BudgetCaller Anonymous(Audience audience) =>
        new(null, audience, PersonaChoice.None, [], [], new HashSet<string>(StringComparer.Ordinal), null);
}

/// <summary>The outfits to show (accessory added) and what they are; <c>Closest</c> is true for the one over-budget answer.</summary>
internal sealed record BudgetResult(OutfitContext Context, IReadOnlyList<OutfitCandidate> Outfits, bool OverBudget, BudgetCatalog Catalog);

internal sealed record BudgetOfferResponse(Guid OfferId, SellerRef? Seller, decimal Price, decimal? OriginalPrice, string GoPath);

/// <summary>
/// One piece. A wardrobe piece costs 0 and has <c>WardrobeItemId</c>; a catalog piece has <c>ProductId</c>, its <c>Offer</c> (the cheapest in the caller's
/// size, or the cheapest at all with <c>SizeUnknown</c>) and, when the size is known, <c>Size</c>.
/// </summary>
internal sealed record BudgetPieceResponse(
    OutfitSlot Slot,
    OutfitItemRole Role,
    PieceSource Source,
    Guid? WardrobeItemId,
    Guid? ProductId,
    string Name,
    string? CategorySlug,
    ProductColor Color,
    string? ImageUrl,
    decimal Price,
    BudgetOfferResponse? Offer,
    string? Size,
    bool? SizeUnknown);

/// <summary>
/// A budget outfit. <c>Id</c> and <c>Status</c> exist for a signed-in caller (the outfit is stored: save, worn, feedback and dismiss work on it).
/// <c>Savings</c> appears only when a seller shows a real crossed-out price; <c>OverBudgetBy</c> only on the one over-budget answer.
/// </summary>
internal sealed record BudgetOutfitResponse(
    Guid? Id,
    OutfitStatus? Status,
    OutfitCompleteness Completeness,
    int Score,
    string Rule,
    IReadOnlyList<BudgetPieceResponse> Pieces,
    decimal TotalPrice,
    decimal RemainingBudget,
    int OwnedCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Savings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? OverBudgetBy,
    OutfitNarrationResponse Narration);

/// <summary>
/// <c>Outfits</c> fit <c>Budget</c>; when none does, <c>OverBudget</c> is true and the single outfit is the closest one (<c>Message</c> says by how much),
/// or the list is empty and <c>Message</c> says so. <c>Weather</c> is the forecast used, if any (signed-in callers with a city).
/// </summary>
internal sealed record BudgetOutfitsResponse(
    decimal Budget,
    DateOnly Date,
    Occasion? Occasion,
    Audience Audience,
    bool UseWardrobe,
    int Seed,
    OutfitWeatherResponse? Weather,
    bool OverBudget,
    string? Message,
    IReadOnlyList<BudgetOutfitResponse> Outfits);

/// <summary>
/// "Bütçeye göre kombin", shared by <c>POST /me/outfits/budget</c> and the anonymous <c>POST /outfits/budget</c>: candidates from the catalog
/// (<see cref="BudgetCandidates"/>), the engine enumerates and scores, <see cref="BudgetSearch"/> picks. Design: docs/design/budget-and-trips.md.
/// </summary>
internal static class BudgetOutfits
{
    public const string ClickSource = "budget";

    public const decimal MinBudget = 100;

    public const decimal MaxBudget = 100_000;

    public const int DefaultCount = 3;

    public const string NothingFitsMessage = "Bu bütçeyle sana uygun bir kombin çıkmadı; bütçeyi biraz artırmayı dene.";

    public static string OverBudgetMessage(decimal total) => $"Bu bütçeyle tam bir kombin çıkmadı; en yakını {Copy.Money(total)}.";

    public static async Task<BudgetResult> SearchAsync(KombinDbContext db, BudgetCaller caller, BudgetAsk ask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(ask);

        var taste = caller.Persona.Taste;
        var weather = caller.Weather is null ? WeatherRules.FromSeason(ask.Date) : WeatherRules.From(caller.Weather);
        var context = OutfitContext.Create(ask.Occasion, taste, weather, ask.Date);

        // The request names the budget, so the persona's own budget band does not apply here.
        var slots = context.Weather.Outerwear == OuterwearPolicy.Forbidden ? OutfitSlots.Core : [.. OutfitSlots.Core, OutfitSlot.Outerwear];
        var cap = BudgetSearch.Reach(ask.Budget);
        var catalog = await BudgetCandidates.LoadAsync(
            db,
            [.. slots.Select(slot => new BudgetQuery(slot, context.BandMin, context.BandMax, caller.Audience, taste.AvoidedColors, cap, caller.Owned))],
            caller.UserId,
            caller.Persona.Scoring,
            cancellationToken);

        var shortlist = slots.SelectMany(slot => BudgetSearch.Shortlist(catalog.Pieces.Where(piece => piece.Slot == slot), context)).ToList();
        var wardrobe = ask.UseWardrobe ? caller.Wardrobe : [];

        var acceptable = OutfitGenerator.Generate(new GenerationRequest(
            wardrobe, shortlist, context, null, BudgetSearch.MaxGaps, caller.Dismissed, BudgetSearch.CatalogPerSlot, cap));
        var pick = BudgetSearch.Pick(acceptable, ask.Budget, ask.Count, ask.Seed);

        var chosen = pick.Outfits.Count > 0 ? pick.Outfits : pick.Closest is { } closest ? [closest] : [];
        var outfits = chosen
            .Select(outfit => wardrobe.Count == 0 ? outfit.Candidate : OutfitGenerator.WithAccessory(outfit.Candidate, wardrobe, context, null))
            .ToList();

        return new BudgetResult(context, outfits, pick.Outfits.Count == 0 && pick.Closest is not null, catalog);
    }

    /// <summary>Template lines (or LLM lines cached for the same summary) for outfits that are not stored: the anonymous answer.</summary>
    public static async Task<List<OutfitNarrationResponse>> NarrateAsync(
        KombinDbContext db, IReadOnlyList<OutfitCandidate> outfits, OutfitContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outfits);

        var summaries = outfits.Select(outfit => OutfitSummary.From(outfit, OutfitKind.Budget, context, null)).ToList();
        var cached = await NarrationCache.LoadAsync(db, [.. summaries.Select(summary => summary.Hash)], now, cancellationToken);

        return
        [
            .. summaries.Select(summary => cached.TryGetValue(summary.Hash, out var text)
                ? new OutfitNarrationResponse(text, NarrationProvider.Anthropic)
                : new OutfitNarrationResponse(TemplateNarrator.Narrate(summary), NarrationProvider.Template)),
        ];
    }

    /// <summary>
    /// Builds the answer. <paramref name="stored"/> (signed-in callers) holds the stored rows in the order of the result's outfits; null for an
    /// anonymous caller, who gets <paramref name="narrations"/> instead.
    /// </summary>
    public static async Task<BudgetOutfitsResponse> RespondAsync(
        KombinDbContext db,
        IObjectStorage storage,
        BudgetAsk ask,
        BudgetCaller caller,
        BudgetResult result,
        IReadOnlyList<Outfit>? stored,
        IReadOnlyList<OutfitNarrationResponse>? narrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(result);

        var pieces = result.Outfits.SelectMany(outfit => outfit.Pieces).ToList();
        var wardrobeIds = pieces.Where(piece => piece.Source == PieceSource.Wardrobe).Select(piece => piece.Id).Distinct().ToList();
        var productIds = pieces.Where(piece => piece.Source == PieceSource.Catalog).Select(piece => piece.Id).Distinct().ToList();
        var offerIds = pieces.Select(piece => piece.OfferId).OfType<Guid>().Distinct().ToList();

        var wardrobeItems = wardrobeIds.Count == 0 ? [] : await db.WardrobeItems.AsNoTracking().Where(item => wardrobeIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var summaries = (await WardrobeItemResponses.SummarizeAsync(db, storage, wardrobeItems, cancellationToken)).ToDictionary(summary => summary.Id);

        var products = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.Name, BrandName = product.Brand.Name, product.PrimaryImageUrl })
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var sellers = await db.Offers.AsNoTracking()
            .Where(offer => offerIds.Contains(offer.Id))
            .Select(offer => new { offer.Id, offer.Seller.Name, offer.Seller.Slug })
            .ToDictionaryAsync(offer => offer.Id, offer => new SellerRef(offer.Name, offer.Slug), cancellationToken);

        BudgetPieceResponse Piece(OutfitPiece piece)
        {
            if (piece.Source == PieceSource.Wardrobe)
            {
                var summary = summaries.GetValueOrDefault(piece.Id);
                var name = summary?.Name ?? TurkishGrammar.Capitalize(TurkishGrammar.Phrase(piece.Color, piece.CategorySlug));

                return new BudgetPieceResponse(
                    piece.Slot, OutfitItemRole.Wardrobe, PieceSource.Wardrobe, piece.Id, null, name, piece.CategorySlug, piece.Color, summary?.ThumbUrl, 0, null, null, null);
            }

            var product = products.GetValueOrDefault(piece.Id);
            var offer = result.Catalog.Offers[piece.Id];

            return new BudgetPieceResponse(
                piece.Slot,
                OutfitItemRole.Gap,
                PieceSource.Catalog,
                null,
                piece.Id,
                product is null ? "Ürün" : Copy.ProductDisplayName(product.BrandName, product.Name),
                piece.CategorySlug,
                piece.Color,
                product?.PrimaryImageUrl,
                offer.Price,
                new BudgetOfferResponse(offer.OfferId, sellers.GetValueOrDefault(offer.OfferId), offer.Price, offer.Savings is null ? null : offer.OriginalPrice, OutfitResponses.GoPath(offer.OfferId, OutfitKind.Budget)),
                offer.Size,
                offer.SizeUnknown);
        }

        var outfits = new List<BudgetOutfitResponse>(result.Outfits.Count);
        for (var index = 0; index < result.Outfits.Count; index++)
        {
            var candidate = result.Outfits[index];
            var row = stored?[index];
            var bought = candidate.Pieces.Where(piece => piece.Source == PieceSource.Catalog).ToList();
            var total = bought.Sum(piece => result.Catalog.Offers[piece.Id].Price);
            var savings = bought.Sum(piece => result.Catalog.Offers[piece.Id].Savings ?? 0);
            var narration = row is null ? narrations![index] : new OutfitNarrationResponse(row.Narration, row.NarrationProvider);

            outfits.Add(new BudgetOutfitResponse(
                row?.Id,
                row?.Status,
                candidate.Completeness(null),
                candidate.Score,
                candidate.Evaluation.Rule.ToString(),
                [.. candidate.Pieces.OrderBy(piece => piece.Slot).Select(Piece)],
                total,
                Math.Max(0, ask.Budget - total),
                candidate.Pieces.Count(piece => piece.Source == PieceSource.Wardrobe),
                savings > 0 ? savings : null,
                total > ask.Budget ? total - ask.Budget : null,
                narration));
        }

        var message = result.OverBudget ? OverBudgetMessage(outfits[0].TotalPrice) : outfits.Count == 0 ? NothingFitsMessage : null;

        return new BudgetOutfitsResponse(
            ask.Budget,
            ask.Date,
            ask.Occasion,
            caller.Audience,
            ask.UseWardrobe && caller.UserId is not null,
            ask.Seed,
            caller.Weather is null ? null : OutfitWeatherResponse.From(caller.Weather),
            result.OverBudget,
            message,
            outfits);
    }
}
