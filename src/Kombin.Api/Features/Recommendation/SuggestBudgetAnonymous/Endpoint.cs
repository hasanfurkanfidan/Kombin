using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.SuggestBudgetAnonymous;

/// <summary>
/// The acquisition variant: no wardrobe, no sizes, no persona, nothing stored. <c>Audience</c> (Women, Men or Unisex) is required; the rest is as
/// in <c>POST /me/outfits/budget</c>.
/// </summary>
internal sealed record AnonymousBudgetOutfitsRequest(decimal? Budget, string? Audience, string? Occasion, string? Date, int? Seed, int? Count);

internal static class SuggestBudgetAnonymousEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/budget", Handle)
            .WithName("SuggestBudgetOutfitsAnonymous")
            .AllowAnonymous()
            .WithValidation<AnonymousBudgetOutfitsRequest>()
            .RequireRateLimiting(RateLimitPolicies.Budget)
            .Produces<BudgetOutfitsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Up to <c>count</c> outfits made only of catalog pieces that fit the budget, for the given audience; the season of the date stands in for the
    /// weather. A bearer token changes nothing here: signed-in callers use <c>/me/outfits/budget</c>.
    /// </summary>
    private static async Task<IResult> Handle(
        AnonymousBudgetOutfitsRequest request, KombinDbContext db, IClock clock, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var audience = EnumNames.TryParse<Audience>(request.Audience, out var parsed) ? parsed : Audience.Unisex;
        var ask = new BudgetAsk(
            request.Budget!.Value,
            EnumNames.TryParse<Occasion>(request.Occasion, out var occasion) ? occasion : null,
            OutfitDates.Resolve(request.Date, OutfitSources.Today(now))!.Value,
            UseWardrobe: false,
            request.Seed ?? 0,
            request.Count ?? BudgetOutfits.DefaultCount);

        var caller = BudgetCaller.Anonymous(audience);
        var result = await BudgetOutfits.SearchAsync(db, caller, ask, cancellationToken);
        var narrations = await BudgetOutfits.NarrateAsync(db, result.Outfits, result.Context, now, cancellationToken);

        return TypedResults.Ok(await BudgetOutfits.RespondAsync(db, storage, ask, caller, result, null, narrations, cancellationToken));
    }
}
