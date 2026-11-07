using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;
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
        Occasion? occasion = EnumNames.TryParse<Occasion>(query.Occasion, out var parsed) ? parsed : null;

        var context = await HowToWear.LoadAsync(db, currentUser.UserId, query.PersonaId, clock.UtcNow, cancellationToken);
        if (context is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        var result = await HowToWear.SuggestAsync(
            db,
            store,
            context,
            new HowToWearAnchor(query.ProductId, query.WardrobeItemId),
            query.Count ?? DefaultCount,
            query.Seed ?? 0,
            occasion,
            cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToHttpResult();
        }

        var (piece, outfits, shopping) = result.Value;

        return TypedResults.Ok(new HowToWearResponse(
            piece.Source == PieceSource.Catalog ? piece.Id : null,
            piece.Source == PieceSource.Wardrobe ? piece.Id : null,
            await OutfitResponses.BuildAsync(db, storage, outfits, cancellationToken),
            shopping ? ShoppingMessage : outfits.Count == 0 ? NothingFitsMessage : null));
    }
}
