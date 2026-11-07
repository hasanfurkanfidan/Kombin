using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.TryWithWardrobe;

/// <summary>All optional: <c>Seed</c> re-ranks near-equal outfits, <c>Occasion</c> and <c>PersonaId</c> as on how-to-wear.</summary>
internal sealed record TryWithWardrobeRequest(int? Seed, string? Occasion, Guid? PersonaId);

/// <summary>
/// The outfits built around one tagged product. <c>Owned</c>: the caller already has it, so the anchor is their own piece. The anchor's offer
/// keeps the post attribution (<c>goPath</c> with <c>post=</c>); gap pieces carry their own cheapest offer.
/// </summary>
internal sealed record TryGroup(Guid TagId, Guid ProductId, bool Owned, IReadOnlyList<OutfitResponse> Outfits, string? Message);

internal sealed record TryWithWardrobeResponse(Guid PostId, IReadOnlyList<TryGroup> Groups);

internal static class TryWithWardrobeEndpoint
{
    /// <summary>At most this many tagged products become anchors (top, bottom, layer, shoes, accessory first).</summary>
    public const int MaxAnchors = 3;

    public const int OutfitsPerAnchor = 2;

    private static readonly OutfitSlot[] SlotOrder = [OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Outerwear, OutfitSlot.Shoes, OutfitSlot.Accessory];

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/try", Handle)
            .WithName("TryPostWithWardrobe")
            .WithValidation<TryWithWardrobeRequest>()
            .Produces<TryWithWardrobeResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// "Dolabımla dene": each tagged catalog product (a product tag, or a wardrobe tag whose piece came from the catalog) is a how-to-wear
    /// anchor against the caller's wardrobe, through the same engine as <c>GET /me/outfits/how-to-wear</c>. Unknown tags and the author's
    /// own photographed pieces cannot be anchors (nobody can buy them).
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id,
        TryWithWardrobeRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        OutfitStore store,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var post = await PostAccess.FindVisibleAsync(db, id, userId, tracking: false, cancellationToken);
        if (post is null)
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        var confirmed = post.Tags.Where(tag => tag.ConfirmedAt is not null).ToList();
        var itemIds = confirmed.Select(tag => tag.WardrobeItemId).OfType<Guid>().ToList();
        var catalogPieces = await db.WardrobeItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && item.ProductId != null)
            .ToDictionaryAsync(item => item.Id, item => item.ProductId!.Value, cancellationToken);

        var tagged = confirmed
            .Select(tag => (Tag: tag, ProductId: tag.Kind == PostTagKind.Product ? tag.ProductId : tag.WardrobeItemId is { } itemId ? catalogPieces.GetValueOrDefault(itemId) as Guid? : null))
            .Where(pair => pair.ProductId is not null && pair.ProductId != Guid.Empty)
            .ToList();
        var productIds = tagged.Select(pair => pair.ProductId!.Value).Distinct().ToList();
        var slugs = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.Category.Slug })
            .ToDictionaryAsync(product => product.Id, product => product.Slug, cancellationToken);

        var anchors = tagged
            .Where(pair => slugs.ContainsKey(pair.ProductId!.Value))
            .DistinctBy(pair => pair.ProductId)
            .OrderBy(pair => Array.IndexOf(SlotOrder, OutfitSlots.ForCategorySlug(slugs[pair.ProductId!.Value])))
            .ThenBy(pair => pair.Tag.Position)
            .Take(MaxAnchors)
            .ToList();
        if (anchors.Count == 0)
        {
            return SocialErrors.NothingToTry.ToHttpResult();
        }

        var context = await HowToWear.LoadAsync(db, userId, request.PersonaId, clock.UtcNow, cancellationToken);
        if (context is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        Occasion? occasion = EnumNames.TryParse<Occasion>(request.Occasion, out var parsed) ? parsed : null;
        var groups = new List<TryGroup>();

        foreach (var (tag, productId) in anchors)
        {
            var result = await HowToWear.SuggestAsync(db, store, context, new HowToWearAnchor(productId, null), OutfitsPerAnchor, request.Seed ?? 0, occasion, cancellationToken);
            if (result.IsFailure)
            {
                continue;
            }

            var (piece, outfits, shopping) = result.Value;
            var responses = await OutfitResponses.BuildAsync(db, storage, outfits, cancellationToken);

            groups.Add(new TryGroup(
                tag.Id,
                productId!.Value,
                piece.Source == PieceSource.Wardrobe,
                [.. responses.Select(outfit => WithPostAttribution(outfit, productId.Value, post.Id))],
                shopping ? Recommendation.GetHowToWear.GetHowToWearEndpoint.ShoppingMessage
                    : outfits.Count == 0 ? Recommendation.GetHowToWear.GetHowToWearEndpoint.NothingFitsMessage : null));
        }

        return TypedResults.Ok(new TryWithWardrobeResponse(post.Id, groups));
    }

    /// <summary>The anchor came from the post, so buying it from here is still a click on the post (and its creator).</summary>
    private static OutfitResponse WithPostAttribution(OutfitResponse outfit, Guid productId, Guid postId) =>
        outfit with
        {
            Pieces =
            [
                .. outfit.Pieces.Select(piece => piece.Role == OutfitItemRole.Anchor && piece.ProductId == productId && piece.Offer is { } offer
                    ? piece with { Offer = offer with { GoPath = PostResponses.GoPath(offer.OfferId, postId) } }
                    : piece),
            ],
        };
}
