using System.Globalization;
using System.Text.Json;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Recommendation;

/// <summary>A plan for one day of the trip: <c>Occasion</c> is office, weekend, evening, sport or event.</summary>
internal sealed record TripOccasionRequest(DateOnly? Date, string? Occasion);

/// <summary>
/// "Valiz hazırlama": the destination <c>City</c>, <c>StartDate</c>..<c>EndDate</c> (1-14 days, from today up to a year ahead), optional plans per day,
/// at most <c>MaxPieces</c> packed (3-60), shoes on consecutive days (<c>AllowRepeatShoes</c>, default true), laundry on the way (<c>Laundry</c>,
/// default false). <c>PersonaId</c> follows the catalog convention.
/// </summary>
internal sealed record TripPlanRequest(
    string? City,
    DateOnly? StartDate,
    DateOnly? EndDate,
    IReadOnlyList<TripOccasionRequest>? Occasions,
    int? MaxPieces,
    bool? AllowRepeatShoes,
    bool? Laundry,
    Guid? PersonaId);

// What is stored for a saved trip (recommendation.trips.plan_json): IDs, scores and the weather, no names or images (read live).
internal sealed record TripPieceSnapshot(Guid WardrobeItemId, OutfitSlot Slot, string CategorySlug, ProductColor Color);

internal sealed record TripOutfitSnapshot(IReadOnlyList<TripPieceSnapshot> Pieces, int Score, string Rule, string Narration, NarrationProvider NarrationProvider);

internal sealed record TripDaySnapshot(DateOnly Date, Occasion? Occasion, WeatherSource WeatherSource, WeatherSnapshot? Weather, TripOutfitSnapshot? Outfit);

internal sealed record TripGapSnapshot(OutfitSlot Slot, TripGapReason Reason, IReadOnlyList<DateOnly> Days, Guid? ProductId, Guid? OfferId, decimal? Price);

internal sealed record TripPlanSnapshot(string City, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<TripDaySnapshot> Days, IReadOnlyList<TripGapSnapshot> Gaps, string? Message)
{
    public int Covered => Days.Count(day => day.Outfit is not null);

    public int PieceCount => Days.SelectMany(day => day.Outfit?.Pieces ?? []).Select(piece => piece.WardrobeItemId).Distinct().Count();
}

internal sealed record TripPackItem(Guid WardrobeItemId, string Name, string? CategorySlug, ProductColor Color, string? ImageUrl, int Days, bool Missing);

/// <summary>The packing list of one slot.</summary>
internal sealed record TripPackGroup(OutfitSlot Slot, IReadOnlyList<TripPackItem> Items);

/// <summary>
/// The day's weather and where it came from: <c>Forecast</c> (Open-Meteo, within 16 days), <c>Normal</c> (the climate normals of that calendar day)
/// or <c>Season</c> (neither; the temperatures are then null and <c>Season</c> decided).
/// </summary>
internal sealed record TripWeatherResponse(
    WeatherSource Source,
    double? TempMinC,
    double? TempMaxC,
    int? TemperatureC,
    TemperatureBand? Band,
    bool Rain,
    int? PrecipitationProbability,
    Season Season);

internal sealed record TripOutfitPiece(OutfitSlot Slot, Guid WardrobeItemId, string Name, string? CategorySlug, ProductColor Color, string? ImageUrl, bool Missing);

internal sealed record TripOutfitResponse(IReadOnlyList<TripOutfitPiece> Pieces, int Score, string Rule, OutfitNarrationResponse Narration);

internal sealed record TripDayResponse(DateOnly Date, Occasion? Occasion, TripWeatherResponse Weather, TripOutfitResponse? Outfit);

internal sealed record TripCoverage(int Covered, int Total, int Percent);

internal sealed record TripGapSuggestion(Guid ProductId, string Name, string? CategorySlug, ProductColor Color, string? ImageUrl, OutfitOfferResponse Offer);

/// <summary><c>Days</c> are the trip days the piece would dress. <c>Suggestion</c> is one catalog piece for it (a <c>goPath</c> with <c>src=trip</c>).</summary>
internal sealed record TripGapResponse(OutfitSlot Slot, TripGapReason Reason, string Message, IReadOnlyList<DateOnly> Days, TripGapSuggestion? Suggestion);

/// <summary>
/// A packing plan. <c>Pieces</c> is the packing list grouped by slot, each piece with the number of days it is worn; <c>Days</c> one entry per day
/// with its weather and outfit (null when none could be made); <c>Coverage</c> the days with an outfit; <c>Gaps</c> what is missing and why.
/// A piece removed from the wardrobe since the plan was made is <c>Missing</c>.
/// </summary>
internal sealed record TripPlanResponse(
    string City,
    DateOnly StartDate,
    DateOnly EndDate,
    int PieceCount,
    IReadOnlyList<TripPackGroup> Pieces,
    IReadOnlyList<TripDayResponse> Days,
    TripCoverage Coverage,
    IReadOnlyList<TripGapResponse> Gaps,
    string? Message);

/// <summary>What <see cref="TripPlanning.PlanAsync"/> needs, validated.</summary>
internal sealed record TripAsk(string City, DateOnly Start, DateOnly End, IReadOnlyDictionary<DateOnly, Occasion> Occasions, TripOptions Options, Guid? PersonaId)
{
    public static TripAsk From(TripPlanRequest request, DateOnly? from = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var start = request.StartDate!.Value;
        if (from is { } later && later > start)
        {
            start = later;
        }

        var occasions = (request.Occasions ?? [])
            .Where(entry => entry.Date is not null && EnumNames.TryParse<Occasion>(entry.Occasion, out _))
            .GroupBy(entry => entry.Date!.Value)
            .ToDictionary(group => group.Key, group => EnumNames.TryParse<Occasion>(group.First().Occasion, out var occasion) ? occasion : default);

        return new TripAsk(
            request.City!.Trim(),
            start,
            request.EndDate!.Value,
            occasions,
            new TripOptions(request.MaxPieces, request.AllowRepeatShoes ?? true, request.Laundry ?? false),
            request.PersonaId);
    }
}

/// <summary>
/// "Valiz hazırlama", shared by <c>POST /me/trips/plan</c>, saving and re-planning: the weather of every day, the engine's outfits per day, the
/// <see cref="TripPlanner"/> search, the <see cref="TripGaps"/> and one catalog suggestion per gap. Design: docs/design/budget-and-trips.md.
/// </summary>
internal static class TripPlanning
{
    public const string ClickSource = "trip";

    public const int MaxDays = 14;

    public const int MaxDaysAhead = 365;

    public const int MinPieces = 3;

    public const int MaxPieces = 60;

    public const int MaxSavedTrips = 50;

    public const string ThinWardrobeMessage = "Dolabında bu gezi için yeterli parça yok.";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string PartialMessage(int covered, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"{total} günün {covered} günü için dolabından kombin çıktı; eksik parçalar aşağıda.");

    public static string MaxPiecesMessage(int max) =>
        string.Create(CultureInfo.InvariantCulture, $"{max} parçayla her güne kombin çıkmadı; parça sınırını artırırsan kalan günler de dolar.");

    /// <summary>What a gap's line says, by reason; the slot is named in everyday words.</summary>
    public static string GapMessage(OutfitSlot slot, TripGapReason reason)
    {
        var piece = slot switch
        {
            OutfitSlot.Top => "üst giyim parçası",
            OutfitSlot.Bottom => "alt giyim parçası",
            OutfitSlot.Shoes => "ayakkabı",
            _ => "dış giyim parçası",
        };

        return reason switch
        {
            TripGapReason.Missing => $"Dolabında hiç {piece} yok.",
            TripGapReason.Weather => $"Bu günlerin havasına uygun bir {piece} yok.",
            TripGapReason.Occasion => $"Bu günlerin planına uygun bir {piece} yok.",
            TripGapReason.NoCombination => $"Bu günler için dolabındaki parçalarla bir kombin çıkmadı; bir {piece} işini görür.",
            _ => $"Aynı parçaları üst üste giymemek için bir {piece} daha gerekiyor.",
        };
    }

    /// <summary>The plan, or <c>Profile.PersonaNotFound</c> for someone else's (or an archived) persona.</summary>
    public static async Task<Result<TripPlanSnapshot>> PlanAsync(
        KombinDbContext db, IWeatherProvider weather, IMemoryCache cache, Guid userId, TripAsk ask, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        var persona = await OutfitSources.ChoosePersonaAsync(db, userId, ask.PersonaId, profile?.ActivePersonaId, now, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound;
        }

        var taste = persona.Taste;
        var forecast = await weather.GetDaysAsync(ask.City, ask.Start, ask.End, cancellationToken);
        var days = forecast
            .Select(day => new TripDay(
                day.Date,
                OutfitContext.Create(
                    ask.Occasions.TryGetValue(day.Date, out var occasion) ? occasion : null,
                    taste,
                    day.Snapshot is null ? WeatherRules.FromSeason(day.Date) : WeatherRules.From(day.Snapshot),
                    day.Date)))
            .ToList();

        var wardrobe = await OutfitSources.LoadWardrobeAsync(db, userId, persona.Scoring, cancellationToken);
        var companions = await OutfitCounts.CompanionCountsAsync(db, cache, userId, cancellationToken);

        var options = TripPlanner.OptionsFor(days, wardrobe, companions);
        var plan = TripPlanner.Solve(options, ask.Options);
        var gaps = TripGaps.Analyze(days, wardrobe, options, plan.Outfits, plan.DroppedDays, ask.Options);

        var suggestions = await SuggestAsync(db, userId, profile?.Audience ?? Audience.Unisex, taste, persona.Scoring, days, gaps, cancellationToken);
        var narrations = await NarrateAsync(db, days, plan.Outfits, now, cancellationToken);

        var dayRows = days.Select((day, index) => new TripDaySnapshot(
            day.Date,
            day.Context.Occasion,
            forecast[index].Source,
            forecast[index].Snapshot,
            plan.Outfits[index] is { } outfit
                ? new TripOutfitSnapshot(
                    [.. TripPlanner.Core(outfit).OrderBy(piece => piece.Slot).Select(piece => new TripPieceSnapshot(piece.Id, piece.Slot, piece.CategorySlug, piece.Color))],
                    outfit.Score,
                    outfit.Evaluation.Rule.ToString(),
                    narrations[index]!.Text,
                    narrations[index]!.Provider)
                : null)).ToList();

        var covered = dayRows.Count(day => day.Outfit is not null);
        var message = covered == 0
            ? ThinWardrobeMessage
            : plan.DroppedDays.Count > 0 ? MaxPiecesMessage(ask.Options.MaxPieces!.Value)
            : covered < days.Count ? PartialMessage(covered, days.Count)
            : null;

        return new TripPlanSnapshot(
            ask.City,
            ask.Start,
            ask.End,
            dayRows,
            [.. gaps.Select((gap, index) => new TripGapSnapshot(
                gap.Slot,
                gap.Reason,
                [.. gap.Days.Select(day => days[day].Date)],
                suggestions[index]?.Id,
                suggestions[index]?.OfferId,
                suggestions[index]?.Price))],
            message);
    }

    /// <summary>Builds the answer from a plan; names and images are read now, so a saved plan shows today's wardrobe.</summary>
    public static async Task<TripPlanResponse> RespondAsync(
        KombinDbContext db, IObjectStorage storage, Guid userId, TripPlanSnapshot plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var itemIds = plan.Days.SelectMany(day => day.Outfit?.Pieces ?? []).Select(piece => piece.WardrobeItemId).Distinct().ToList();
        var items = await db.WardrobeItems.AsNoTracking().Where(item => item.UserId == userId && itemIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var summaries = (await WardrobeItemResponses.SummarizeAsync(db, storage, items, cancellationToken)).ToDictionary(summary => summary.Id);

        var productIds = plan.Gaps.Select(gap => gap.ProductId).OfType<Guid>().Distinct().ToList();
        var offerIds = plan.Gaps.Select(gap => gap.OfferId).OfType<Guid>().Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.Name, BrandName = product.Brand.Name, CategorySlug = product.Category.Slug, product.PrimaryColor, product.PrimaryImageUrl })
            .ToDictionaryAsync(product => product.Id, cancellationToken);
        var sellers = await db.Offers.AsNoTracking()
            .Where(offer => offerIds.Contains(offer.Id))
            .Select(offer => new { offer.Id, offer.Seller.Name, offer.Seller.Slug })
            .ToDictionaryAsync(offer => offer.Id, offer => new SellerRef(offer.Name, offer.Slug), cancellationToken);

        (string Name, string? ImageUrl, bool Missing) Describe(TripPieceSnapshot piece)
        {
            var summary = summaries.GetValueOrDefault(piece.WardrobeItemId);

            return (
                summary?.Name ?? TurkishGrammar.Capitalize(TurkishGrammar.Phrase(piece.Color, piece.CategorySlug)),
                summary?.ThumbUrl,
                summary is null || summary.IsArchived);
        }

        var uses = plan.Days.SelectMany(day => day.Outfit?.Pieces ?? []).GroupBy(piece => piece.WardrobeItemId).ToList();
        var groups = uses
            .Select(group => (Piece: group.First(), Days: group.Count()))
            .GroupBy(entry => entry.Piece.Slot)
            .OrderBy(group => group.Key)
            .Select(group => new TripPackGroup(
                group.Key,
                [
                    .. group
                        .Select(entry =>
                        {
                            var (name, image, missing) = Describe(entry.Piece);
                            return new TripPackItem(entry.Piece.WardrobeItemId, name, entry.Piece.CategorySlug, entry.Piece.Color, image, entry.Days, missing);
                        })
                        .OrderByDescending(item => item.Days)
                        .ThenBy(item => item.Name, StringComparer.Ordinal),
                ]))
            .ToList();

        var days = plan.Days.Select(day => new TripDayResponse(
            day.Date,
            day.Occasion,
            Weather(day),
            day.Outfit is { } outfit
                ? new TripOutfitResponse(
                    [
                        .. outfit.Pieces.Select(piece =>
                        {
                            var (name, image, missing) = Describe(piece);
                            return new TripOutfitPiece(piece.Slot, piece.WardrobeItemId, name, piece.CategorySlug, piece.Color, image, missing);
                        }),
                    ],
                    outfit.Score,
                    outfit.Rule,
                    new OutfitNarrationResponse(outfit.Narration, outfit.NarrationProvider))
                : null)).ToList();

        var gaps = plan.Gaps.Select(gap =>
        {
            TripGapSuggestion? suggestion = null;
            if (gap is { ProductId: { } productId, OfferId: { } offerId, Price: { } price })
            {
                var product = products.GetValueOrDefault(productId);
                suggestion = new TripGapSuggestion(
                    productId,
                    product is null ? "Ürün" : Copy.ProductDisplayName(product.BrandName, product.Name),
                    product?.CategorySlug,
                    product?.PrimaryColor ?? ProductColor.Other,
                    product?.PrimaryImageUrl,
                    new OutfitOfferResponse(offerId, sellers.GetValueOrDefault(offerId), price, $"/api/v1/go/{offerId}?src={ClickSource}"));
            }

            return new TripGapResponse(gap.Slot, gap.Reason, GapMessage(gap.Slot, gap.Reason), gap.Days, suggestion);
        }).ToList();

        var total = plan.Days.Count;
        var covered = plan.Covered;

        return new TripPlanResponse(
            plan.City,
            plan.StartDate,
            plan.EndDate,
            plan.PieceCount,
            groups,
            days,
            new TripCoverage(covered, total, total == 0 ? 0 : (int)Math.Round(100.0 * covered / total, MidpointRounding.AwayFromZero)),
            gaps,
            plan.Message);
    }

    private static TripWeatherResponse Weather(TripDaySnapshot day)
    {
        if (day.Weather is not { } snapshot)
        {
            return new TripWeatherResponse(WeatherSource.Season, null, null, null, null, false, null, WeatherRules.SeasonOf(day.Date));
        }

        var conditions = WeatherRules.From(snapshot);

        return new TripWeatherResponse(
            day.WeatherSource,
            snapshot.TempMinC,
            snapshot.TempMaxC,
            conditions.TemperatureC,
            conditions.Band,
            conditions.Rain,
            snapshot.PrecipitationProbability,
            WeatherRules.SeasonOf(day.Date));
    }

    /// <summary>One catalog piece per gap from the narrow prefetch (neutral colours, the first day's band, the audience, the persona's budget), best by piece score.</summary>
    private static async Task<List<OutfitPiece?>> SuggestAsync(
        KombinDbContext db,
        Guid userId,
        Audience audience,
        PersonaTaste taste,
        PersonaScoring? scoring,
        List<TripDay> days,
        List<TripGap> gaps,
        CancellationToken cancellationToken)
    {
        if (gaps.Count == 0)
        {
            return [];
        }

        var owned = await OutfitSources.OwnedProductIdsAsync(db, userId, cancellationToken);
        var colours = GapAnalysis.Neutrals.Where(colour => !taste.AvoidedColors.Contains(colour)).ToList();
        var suggestions = new List<OutfitPiece?>(gaps.Count);

        foreach (var gap in gaps)
        {
            var context = days[gap.Days[0]].Context;
            var pieces = await OutfitSources.LoadCatalogAsync(
                db,
                [new CatalogQuery(gap.Slot, colours, context.BandMin, context.BandMax, audience, taste.BudgetMin, taste.BudgetMax, owned)],
                scoring,
                cancellationToken);

            suggestions.Add(OutfitGenerator.Prune(pieces, context, 1).FirstOrDefault());
        }

        return suggestions;
    }

    /// <summary>The day outfits' lines: an LLM line cached for the same summary, else the template line.</summary>
    private static async Task<List<OutfitNarrationResponse?>> NarrateAsync(
        KombinDbContext db, List<TripDay> days, IReadOnlyList<OutfitCandidate?> outfits, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var summaries = outfits.Select((outfit, index) => outfit is null ? null : OutfitSummary.From(outfit, OutfitKind.Daily, days[index].Context, null)).ToList();
        var cached = await NarrationCache.LoadAsync(db, [.. summaries.OfType<OutfitSummary>().Select(summary => summary.Hash).Distinct()], now, cancellationToken);

        return
        [
            .. summaries.Select(summary => summary is null
                ? null
                : cached.TryGetValue(summary.Hash, out var text)
                    ? new OutfitNarrationResponse(text, NarrationProvider.Anthropic)
                    : new OutfitNarrationResponse(TemplateNarrator.Narrate(summary), NarrationProvider.Template)),
        ];
    }
}
