using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Social.Consent;
using Kombin.Api.Features.Social.CreateUploadUrl;
using Kombin.Api.Features.Social.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.CreateOutfitPost;

internal sealed record CreateOutfitPostRequest(Guid OutfitId);

internal static class CreateOutfitPostEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/from-outfit", Handle)
            .WithName("CreateOutfitPost")
            .Produces<PostResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// A post from one of the caller's saved (or worn) outfits: a collage of its pieces, no photo of a person. Its pieces become proposals
    /// (wardrobe or product, with collage boxes) the author confirms when publishing, like any other tag. The pieces' wardrobe images are
    /// moderated by <c>post-process</c>, enqueued here.
    /// </summary>
    private static async Task<IResult> Handle(
        CreateOutfitPostRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IBackgroundJobClient jobs,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (await ConsentEndpoint.AcceptedAtAsync(db, userId, cancellationToken) is not { } consentAt)
        {
            return SocialErrors.ConsentRequired.ToHttpResult();
        }

        var outfit = await db.Outfits.AsNoTracking()
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.OutfitId && candidate.UserId == userId && candidate.SavedAt != null && candidate.Status != OutfitStatus.Dismissed, cancellationToken);
        if (outfit is null)
        {
            return SocialErrors.OutfitNotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        var post = new Post
        {
            AuthorId = userId,
            Kind = PostKind.Outfit,
            Audience = await CreatePostUploadUrlEndpoint.AudienceOfAsync(db, userId, cancellationToken),
            ConsentVersion = PostingConsent.Current.Version,
            ConsentAt = consentAt,
            OutfitId = outfit.Id,
            ProcessRequestedAt = now,
        };

        // Wardrobe pieces that are still in the wardrobe, and catalog pieces; the collage puts layers and tops first.
        var itemIds = outfit.Items.Select(item => item.WardrobeItemId).OfType<Guid>().ToList();
        var wardrobe = await db.WardrobeItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && item.UserId == userId && !item.IsArchived)
            .Select(item => new { item.Id, item.CategoryId, item.PrimaryColor })
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var productIds = outfit.Items.Select(item => item.ProductId).OfType<Guid>().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.CategoryId, product.PrimaryColor })
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var pieces = outfit.Items
            .Where(item => (item.WardrobeItemId is { } wardrobeId && wardrobe.ContainsKey(wardrobeId)) || (item.ProductId is { } productId && products.ContainsKey(productId)))
            .Take(SocialLimits.MaxTags)
            .ToList();

        var position = 0;
        foreach (var (piece, x, y, w, h) in CollageLayout.Arrange(pieces, item => item.Slot))
        {
            var fromWardrobe = piece.WardrobeItemId is not null;
            var item = fromWardrobe ? wardrobe[piece.WardrobeItemId!.Value] : null;
            var product = fromWardrobe ? null : products[piece.ProductId!.Value];

            post.Tags.Add(new PostTag
            {
                PostId = post.Id,
                Position = position++,
                X = x,
                Y = y,
                W = w,
                H = h,
                Kind = fromWardrobe ? PostTagKind.Wardrobe : PostTagKind.Product,
                Source = PostTagSource.Outfit,
                WardrobeItemId = piece.WardrobeItemId,
                ProductId = piece.ProductId,
                OfferId = piece.OfferId,
                PriceAtTag = piece.Price,
                CategoryId = item?.CategoryId ?? product?.CategoryId,
                Color = item?.PrimaryColor ?? product?.PrimaryColor,
                CreatedAt = now,
            });
        }

        db.Posts.Add(post);
        await db.SaveChangesAsync(cancellationToken);

        jobs.Enqueue<PostProcessJob>(job => job.RunAsync(post.Id, CancellationToken.None));

        var response = await PostResponses.BuildAsync(db, storage, [post], userId, null, cancellationToken);

        return TypedResults.Created($"/api/v1/posts/{post.Id}", response[0]);
    }
}
