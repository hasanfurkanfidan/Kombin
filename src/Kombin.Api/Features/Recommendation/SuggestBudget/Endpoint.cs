using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.SuggestBudget;

/// <summary>
/// <c>Budget</c> in TRY (100-100,000). <c>Occasion</c>: office, weekend, evening, sport or event. <c>Date</c>: today (default), tomorrow or
/// <c>yyyy-MM-dd</c> up to a week ahead. <c>UseWardrobe</c> (default true): owned pieces cost nothing and come first. <c>Seed</c> (default 0): the next
/// seed shows other outfits. <c>Count</c> 1-5 (default 3). <c>PersonaId</c> follows the catalog convention.
/// <c>IncludeShoes</c> (default true): false ("Ayakkabım var") builds outfits without shoes.
/// </summary>
internal sealed record BudgetOutfitsRequest(decimal? Budget, string? Occasion, string? Date, bool? UseWardrobe, int? Seed, int? Count, Guid? PersonaId, bool? IncludeShoes = null);

internal static class SuggestBudgetEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/budget", Handle)
            .WithName("SuggestBudgetOutfits")
            .WithValidation<BudgetOutfitsRequest>()
            .RequireRateLimiting(RateLimitPolicies.Budget)
            .Produces<BudgetOutfitsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Up to <c>count</c> complete outfits that fit the budget, each buying at least one piece, using the caller's wardrobe first and their sizes
    /// when known. Never over the budget; when nothing fits, at most one closest outfit flagged <c>overBudgetBy</c>. The outfits are stored (kind
    /// <c>Budget</c>).
    /// </summary>
    private static async Task<IResult> Handle(
        BudgetOutfitsRequest request,
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
        var ask = new BudgetAsk(
            request.Budget!.Value,
            EnumNames.TryParse<Occasion>(request.Occasion, out var occasion) ? occasion : null,
            OutfitDates.Resolve(request.Date, OutfitSources.Today(now))!.Value,
            request.UseWardrobe ?? true,
            request.Seed ?? 0,
            request.Count ?? BudgetOutfits.DefaultCount,
            request.IncludeShoes ?? true);

        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        var persona = await OutfitSources.ChoosePersonaAsync(db, userId, request.PersonaId, profile?.ActivePersonaId, now, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        var snapshot = profile?.City is { } city ? await weather.GetAsync(city, ask.Date, cancellationToken) : null;
        var caller = new BudgetCaller(
            userId,
            profile?.Audience ?? Audience.Unisex,
            persona,
            ask.UseWardrobe ? await OutfitSources.LoadWardrobeAsync(db, userId, persona.Scoring, cancellationToken) : [],
            await OutfitSources.OwnedProductIdsAsync(db, userId, cancellationToken),
            await OutfitSources.DismissedHashesAsync(db, userId, now, cancellationToken),
            snapshot);

        var result = await BudgetOutfits.SearchAsync(db, caller, ask, cancellationToken);

        var stored = result.Outfits.Count == 0
            ? []
            : await store.PersistAsync(
                new OutfitRequest(userId, persona.Persona?.Id, OutfitKind.Budget, ask.Occasion, ask.Date, ask.Seed, snapshot, result.Context),
                [.. result.Outfits.Select(outfit => new OutfitDraft(outfit, null))],
                now,
                cancellationToken);

        return TypedResults.Ok(await BudgetOutfits.RespondAsync(db, storage, ask, caller, result, stored, null, cancellationToken));
    }
}
