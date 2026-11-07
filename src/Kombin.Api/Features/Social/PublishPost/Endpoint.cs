using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.PublishPost;

/// <summary>
/// One confirmed tag. <c>Kind</c> is <c>Wardrobe</c> (with one of the author's <c>WardrobeItemId</c>), <c>Product</c> (with a
/// <c>ProductId</c>) or <c>Unknown</c> (an optional <c>Label</c>). The box is fractions of the image (clamped into it).
/// </summary>
internal sealed record PublishTag(string? Kind, double X, double Y, double W, double H, Guid? WardrobeItemId, Guid? ProductId, string? Label);

/// <summary><c>Visibility</c> is <c>Private</c>, <c>Followers</c> or <c>Public</c>. <c>Tags</c> replaces every tag and proposal of the post.</summary>
internal sealed record PublishPostRequest(string? Caption, string? Visibility, IReadOnlyList<PublishTag>? Tags);

internal static class PublishPostEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPut("/{id:guid}/publish", Handle)
            .WithName("PublishPost")
            .WithValidation<PublishPostRequest>()
            .Produces<PostResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// The author's confirmation: the tags they confirmed (the only tags anyone else will ever see), the caption and the visibility. A draft
    /// goes live; a post under review stays there until a reviewer approves it; a live post can be published again to change its tags,
    /// caption or visibility. Product tags remember their cheapest offer at this moment; viewers always see the current one.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id, PublishPostRequest request, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var post = await db.Posts.Include(candidate => candidate.Tags)
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.AuthorId == userId && candidate.Status != PostStatus.Deleted, cancellationToken);
        if (post is null)
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        if (post.Status == PostStatus.Processing)
        {
            return SocialErrors.PostProcessing.ToHttpResult();
        }

        if (!PostRules.CanPublish(post.Status))
        {
            return SocialErrors.PostNotPublishable.ToHttpResult();
        }

        var tags = request.Tags ?? [];
        var itemIds = tags.Select(tag => tag.WardrobeItemId).OfType<Guid>().Distinct().ToList();
        var items = await db.WardrobeItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && item.UserId == userId && !item.IsArchived)
            .Select(item => new { item.Id, item.CategoryId, item.PrimaryColor })
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var productIds = tags.Select(tag => tag.ProductId).OfType<Guid>().Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.CategoryId, product.PrimaryColor })
            .ToDictionaryAsync(product => product.Id, cancellationToken);
        if (items.Count != itemIds.Count || products.Count != productIds.Count)
        {
            return SocialErrors.InvalidTagTarget.ToHttpResult();
        }

        var offers = await OutfitSources.CheapestOffersAsync(db, productIds, cancellationToken);
        var now = clock.UtcNow;

        db.PostTags.RemoveRange(post.Tags);
        post.Tags.Clear();

        var position = 0;
        foreach (var tag in tags)
        {
            var kind = EnumNames.TryParse<PostTagKind>(tag.Kind, out var parsed) ? parsed : PostTagKind.Unknown;
            var (x, y, w, h) = TagBoxes.Clamp(tag.X, tag.Y, tag.W, tag.H);
            var item = kind == PostTagKind.Wardrobe ? items[tag.WardrobeItemId!.Value] : null;
            var product = kind == PostTagKind.Product ? products[tag.ProductId!.Value] : null;
            var offer = product is not null && offers.TryGetValue(product.Id, out var cheapest) ? cheapest : ((Guid OfferId, decimal Price)?)null;

            post.Tags.Add(new PostTag
            {
                PostId = post.Id,
                Position = position++,
                X = x,
                Y = y,
                W = w,
                H = h,
                Kind = kind,
                Source = PostTagSource.User,
                WardrobeItemId = item?.Id,
                ProductId = product?.Id,
                OfferId = offer?.OfferId,
                PriceAtTag = offer?.Price,
                Label = string.IsNullOrWhiteSpace(tag.Label) ? null : tag.Label.Trim(),
                CategoryId = item?.CategoryId ?? product?.CategoryId,
                Color = item?.PrimaryColor ?? product?.PrimaryColor,
                ConfirmedAt = now,
                CreatedAt = now,
            });
        }

        post.Caption = string.IsNullOrWhiteSpace(request.Caption) ? null : request.Caption.Trim();
        post.Visibility = EnumNames.TryParse<PostVisibility>(request.Visibility, out var visibility) ? visibility : PostVisibility.Private;
        post.ConfirmedAt = now;
        post.Status = PostRules.StatusAfterConfirmation(post.Status);
        if (post.Status == PostStatus.Live)
        {
            post.PublishedAt ??= now;
        }

        await db.SaveChangesAsync(cancellationToken);

        var response = await PostResponses.BuildAsync(db, storage, [post], userId, null, cancellationToken);

        return TypedResults.Ok(response[0]);
    }
}
