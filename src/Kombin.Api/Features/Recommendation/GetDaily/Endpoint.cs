using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.GetDaily;

/// <summary>
/// <c>Date</c>: <c>today</c> (default), <c>tomorrow</c> (the home card) or <c>yyyy-MM-dd</c> up to a week ahead. <c>Occasion</c>: office,
/// weekend, evening, sport or event. <c>Seed</c> (default 0): "another one" is the next seed. <c>PersonaId</c> follows the catalog
/// convention (default: the active persona).
/// </summary>
internal sealed record DailyOutfitQuery(string? Date, string? Occasion, int? Seed, Guid? PersonaId);

/// <summary>
/// <c>Outfit</c> is null only when nothing wearable could be built (then <c>Message</c> says so). <c>WardrobeEmpty</c> means the outfits
/// are catalog shopping ideas (<c>kind</c> Shopping, <c>completeness</c> CatalogOnly). <c>Weather</c> is the forecast used, if any.
/// </summary>
internal sealed record DailyOutfitResponse(
    OutfitResponse? Outfit,
    IReadOnlyList<OutfitResponse> Alternatives,
    OutfitWeatherResponse? Weather,
    bool WardrobeEmpty,
    string? Message);

internal static class GetDailyEndpoint
{
    public const int Alternatives = 2;

    /// <summary>Gap slots in a shopping outfit: every slot may come from the catalog.</summary>
    public const int ShoppingMaxGaps = 4;

    /// <summary>Shopping ideas use the neutrals plus the persona's few favourite colours.</summary>
    public const int ShoppingColours = 3;

    public const string EmptyWardrobeMessage = "Dolabın henüz boş: bunlar katalogdan alışveriş fikirleri. Parça ekledikçe kombinler dolabından gelecek.";

    public const string NothingFitsMessage = "Bu gün için dolabından uygun bir kombin çıkmadı; birkaç parça daha eklemeyi dene.";

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/daily", Handle)
            .WithName("GetDailyOutfit")
            .WithValidation<DailyOutfitQuery>()
            .Produces<DailyOutfitResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// One outfit for the day, mostly from the wardrobe, plus two alternatives that differ in at least two pieces. A catalog piece is
    /// proposed only when the wardrobe cannot make a wearable outfit on its own, and it is marked (<c>role</c> Gap, <c>NeedsOne</c>).
    /// </summary>
    private static async Task<IResult> Handle(
        [AsParameters] DailyOutfitQuery query,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IWeatherProvider weather,
        IObjectStorage storage,
        OutfitStore store,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;
        var date = OutfitDates.Resolve(query.Date, OutfitSources.Today(now))!.Value;
        Occasion? occasion = EnumNames.TryParse<Occasion>(query.Occasion, out var parsed) ? parsed : null;
        var seed = query.Seed ?? 0;

        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        var persona = await OutfitSources.ChoosePersonaAsync(db, userId, query.PersonaId, profile?.ActivePersonaId, now, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        var snapshot = profile?.City is { } city ? await weather.GetAsync(city, date, cancellationToken) : null;
        var taste = persona.Taste;
        var context = OutfitContext.Create(occasion, taste, snapshot is null ? WeatherRules.FromSeason(date) : WeatherRules.From(snapshot), date);
        var scoring = persona.Scoring;

        var wardrobe = await OutfitSources.LoadWardrobeAsync(db, userId, scoring, cancellationToken);
        var dismissed = await OutfitSources.DismissedHashesAsync(db, userId, now, cancellationToken);
        var wardrobeEmpty = !wardrobe.Any(piece => piece.Slot != OutfitSlot.Accessory);

        List<OutfitCandidate> ranked;
        if (wardrobeEmpty)
        {
            var colours = GapAnalysis.Neutrals.Concat(persona.PreferredColors.Take(ShoppingColours)).Where(colour => !taste.AvoidedColors.Contains(colour)).ToHashSet();
            var catalog = await LoadGapsAsync(db, userId, context, taste, profile?.Audience ?? Audience.Unisex, colours, scoring, cancellationToken);
            ranked = OutfitGenerator.Generate(new GenerationRequest([], catalog, context, null, ShoppingMaxGaps, dismissed));
        }
        else
        {
            ranked = OutfitGenerator.Generate(new GenerationRequest(wardrobe, [], context, null, 0, dismissed));

            // The commercial moment, kept honest: the catalog is asked only when the wardrobe alone cannot dress the day, and then for
            // one neutral piece, the kind that goes with what the user owns.
            if (ranked.Count == 0)
            {
                var colours = GapAnalysis.Neutrals.Where(colour => !taste.AvoidedColors.Contains(colour)).ToHashSet();
                var catalog = await LoadGapsAsync(db, userId, context, taste, profile?.Audience ?? Audience.Unisex, colours, scoring, cancellationToken);
                ranked = OutfitGenerator.Generate(new GenerationRequest(wardrobe, catalog, context, null, 1, dismissed));
            }
        }

        var drafts = OutfitGenerator.PickDaily(ranked, seed, date, Alternatives)
            .Select(candidate => new OutfitDraft(wardrobeEmpty ? candidate : OutfitGenerator.WithAccessory(candidate, wardrobe, context, null), null))
            .ToList();

        var request = new OutfitRequest(userId, persona.Persona?.Id, wardrobeEmpty ? OutfitKind.Shopping : OutfitKind.Daily, occasion, date, seed, snapshot, context);
        var outfits = drafts.Count == 0 ? [] : await store.PersistAsync(request, drafts, now, cancellationToken);
        var responses = await OutfitResponses.BuildAsync(db, storage, outfits, cancellationToken);

        return TypedResults.Ok(new DailyOutfitResponse(
            responses.FirstOrDefault(),
            [.. responses.Skip(1)],
            snapshot is null ? null : OutfitWeatherResponse.From(snapshot),
            wardrobeEmpty,
            wardrobeEmpty ? EmptyWardrobeMessage : responses.Count == 0 ? NothingFitsMessage : null));
    }

    /// <summary>Catalog candidates for every slot the day needs (the layer only when it is allowed), in the context's band and the persona's budget.</summary>
    private static async Task<List<OutfitPiece>> LoadGapsAsync(
        KombinDbContext db,
        Guid userId,
        OutfitContext context,
        PersonaTaste taste,
        Audience audience,
        IReadOnlyCollection<ProductColor> colours,
        PersonaScoring? scoring,
        CancellationToken cancellationToken)
    {
        var owned = await OutfitSources.OwnedProductIdsAsync(db, userId, cancellationToken);
        var slots = context.Weather.Outerwear == OuterwearPolicy.Forbidden ? OutfitSlots.Core : [.. OutfitSlots.Core, OutfitSlot.Outerwear];

        return await OutfitSources.LoadCatalogAsync(
            db,
            slots.Select(slot => new CatalogQuery(slot, colours, context.BandMin, context.BandMax, audience, taste.BudgetMin, taste.BudgetMax, owned)),
            scoring,
            cancellationToken);
    }
}
