using System.Text.Json.Serialization;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Social;

/// <summary><c>Handle</c> and <c>IsCreator</c> come from the creator profile; other users have only a display name.</summary>
internal sealed record PostAuthorResponse(Guid Id, string DisplayName, string? Handle, bool IsCreator);

/// <summary>Private objects behind presigned GETs valid for an hour: cache them by post ID, not by URL. <c>Aspect</c> is width / height.</summary>
internal sealed record PostImageResponse(string Url, string? PreviewUrl, double? Aspect);

/// <summary>The product's current cheapest sellable offer (effective price). <c>GoPath</c> is the only way to open it; it records the click for the post.</summary>
internal sealed record TagOfferResponse(Guid OfferId, SellerRef? Seller, decimal Price, string GoPath);

/// <summary>
/// A confirmed tag. <c>InWardrobe</c> is the viewer's: <c>exact</c> (they own that product, or it is their own piece), <c>similar</c> (they own a
/// ready piece of the same category and colour), null otherwise or when signed out.
/// </summary>
internal sealed record PostTagResponse(
    Guid Id,
    PostTagKind Kind,
    double X,
    double Y,
    double W,
    double H,
    string Name,
    string? CategorySlug,
    ProductColor? Color,
    string? ImageUrl,
    Guid? ProductId,
    Guid? WardrobeItemId,
    TagOfferResponse? Offer,
    string? InWardrobe);

/// <summary>An unconfirmed proposal (the detector's guess or an outfit piece), shown to the author only, in the tag editor.</summary>
internal sealed record PostProposalResponse(
    Guid Id,
    PostTagSource Source,
    double X,
    double Y,
    double W,
    double H,
    string? CategorySlug,
    ProductColor? Color,
    double? Confidence,
    Guid? WardrobeItemId,
    Guid? ProductId);

/// <summary>The band under the photo: "Bu kombin toplam <c>TotalPrice</c> ₺ · <c>PieceCount</c> parça · <c>OwnedCount</c>'si dolabında var".</summary>
internal sealed record PostSummaryResponse(decimal? TotalPrice, int PieceCount, int? OwnedCount);

/// <summary>
/// A post as the caller may see it. <c>Image</c> is null for an outfit post (lay the tags' images out as a collage) and before processing.
/// <c>MatchScore</c> is the viewer's persona match of the product tags. <c>Disclosure</c> is set on creators' posts and must be shown.
/// <c>StatusMessage</c> and <c>Proposals</c> are for the author only. <c>Reason</c> appears in the feed.
/// </summary>
internal sealed record PostResponse(
    Guid Id,
    PostKind Kind,
    PostStatus Status,
    PostVisibility Visibility,
    PostAuthorResponse Author,
    string? Caption,
    PostImageResponse? Image,
    IReadOnlyList<PostTagResponse> Tags,
    PostSummaryResponse Summary,
    int LikeCount,
    bool LikedByMe,
    bool FollowingAuthor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MatchScore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Disclosure,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StatusMessage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PostProposalResponse>? Proposals,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FeedReason? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt);

/// <summary>Builds post responses with a fixed number of queries per page, whatever its size.</summary>
internal static class PostResponses
{
    public const string Exact = "exact";

    public const string Similar = "similar";

    /// <summary>The <c>/go</c> path of an offer seen on a post: the redirect attributes the click to the post (and its creator).</summary>
    public static string GoPath(Guid offerId, Guid postId) => $"/api/v1/go/{offerId}?src=feed&post={postId}";

    /// <summary>
    /// <paramref name="posts"/> must have their <c>Tags</c> loaded. <paramref name="scoring"/> is the viewer's persona (null: no match score);
    /// <paramref name="reasons"/> the feed's reason per post.
    /// </summary>
    public static async Task<List<PostResponse>> BuildAsync(
        KombinDbContext db,
        IObjectStorage storage,
        IReadOnlyList<Post> posts,
        Guid? viewerId,
        PersonaScoring? scoring,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, FeedReason>? reasons = null)
    {
        ArgumentNullException.ThrowIfNull(posts);

        if (posts.Count == 0)
        {
            return [];
        }

        var postIds = posts.Select(post => post.Id).ToList();
        var authorIds = posts.Select(post => post.AuthorId).Distinct().ToList();
        var tags = posts.SelectMany(post => post.Tags).ToList();
        var confirmed = tags.Where(tag => tag.ConfirmedAt is not null).ToList();

        var authors = await db.Users.AsNoTracking()
            .Where(user => authorIds.Contains(user.Id))
            .Select(user => new { user.Id, user.DisplayName })
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        var creators = await db.CreatorProfiles.AsNoTracking()
            .Where(creator => authorIds.Contains(creator.UserId))
            .ToDictionaryAsync(creator => creator.UserId, cancellationToken);

        // Wardrobe tags show the author's piece (public thumbnail); a piece that came from the catalog can be bought like a product tag.
        var itemIds = tags.Select(tag => tag.WardrobeItemId).OfType<Guid>().Distinct().ToList();
        var items = await db.WardrobeItems.AsNoTracking().Where(item => itemIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var itemSummaries = (await WardrobeItemResponses.SummarizeAsync(db, storage, items, cancellationToken)).ToDictionary(summary => summary.Id);
        var itemsById = items.ToDictionary(item => item.Id);

        var productIds = confirmed.Select(tag => tag.ProductId)
            .Concat(items.Select(item => item.ProductId))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var products = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new ProductFacts(
                product.Id,
                product.Name,
                product.Brand.Name,
                product.CategoryId,
                product.Category.Slug,
                product.PrimaryColor,
                product.Pattern,
                product.Fit,
                product.Formality,
                product.Seasons,
                product.PrimaryImageUrl))
            .ToDictionaryAsync(product => product.Id, cancellationToken);
        var offers = await OutfitSources.CheapestOffersAsync(db, productIds, cancellationToken);
        var offerIds = offers.Values.Select(offer => offer.OfferId).ToList();
        var sellers = await db.Offers.AsNoTracking()
            .Where(offer => offerIds.Contains(offer.Id))
            .Select(offer => new { offer.Id, offer.Seller.Name, offer.Seller.Slug })
            .ToDictionaryAsync(offer => offer.Id, offer => new SellerRef(offer.Name, offer.Slug), cancellationToken);

        var categoryIds = tags.Select(tag => tag.CategoryId).OfType<Guid>().Distinct().ToList();
        var categorySlugs = await db.Categories.AsNoTracking()
            .Where(category => categoryIds.Contains(category.Id))
            .ToDictionaryAsync(category => category.Id, category => category.Slug, cancellationToken);

        var viewer = viewerId is { } id ? await ViewerWardrobe.LoadAsync(db, id, cancellationToken) : null;
        var liked = viewerId is { } liker
            ? (await db.PostLikes.AsNoTracking().Where(like => like.UserId == liker && postIds.Contains(like.PostId)).Select(like => like.PostId).ToListAsync(cancellationToken)).ToHashSet()
            : [];
        var followed = viewerId is { } follower
            ? (await db.Follows.AsNoTracking().Where(follow => follow.FollowerId == follower && authorIds.Contains(follow.FolloweeId)).Select(follow => follow.FolloweeId).ToListAsync(cancellationToken)).ToHashSet()
            : [];

        PostTagResponse Tag(Post post, PostTag tag)
        {
            var item = tag.WardrobeItemId is { } wardrobeId ? itemsById.GetValueOrDefault(wardrobeId) : null;
            var productId = tag.Kind == PostTagKind.Product ? tag.ProductId : item?.ProductId;
            var product = productId is { } pid ? products.GetValueOrDefault(pid) : null;
            var offer = productId is { } offeredId && offers.TryGetValue(offeredId, out var cheapest)
                ? new TagOfferResponse(cheapest.OfferId, sellers.GetValueOrDefault(cheapest.OfferId), cheapest.Price, GoPath(cheapest.OfferId, post.Id))
                : null;

            string name;
            string? slug;
            ProductColor? color;
            string? image;
            switch (tag.Kind)
            {
                case PostTagKind.Product when product is not null:
                    name = Copy.ProductDisplayName(product.BrandName, product.Name);
                    slug = product.CategorySlug;
                    color = product.Color;
                    image = product.ImageUrl;
                    break;
                case PostTagKind.Wardrobe when item is not null:
                    var summary = itemSummaries.GetValueOrDefault(item.Id);
                    slug = summary?.CategorySlug;
                    color = item.PrimaryColor;
                    name = item.Name ?? (product is not null ? Copy.ProductDisplayName(product.BrandName, product.Name) : Describe(item.PrimaryColor, slug));
                    image = summary?.ThumbUrl;
                    break;
                default:
                    slug = tag.CategoryId is { } categoryId ? categorySlugs.GetValueOrDefault(categoryId) : null;
                    color = tag.Color;
                    name = tag.Label ?? Describe(tag.Color ?? ProductColor.Other, slug);
                    image = null;
                    break;
            }

            var inWardrobe = viewer?.Owns(viewerId == post.AuthorId && item is not null, productId, CategoryOf(tag, product, item), color);

            return new PostTagResponse(tag.Id, tag.Kind, tag.X, tag.Y, tag.W, tag.H, name, slug, color, image, tag.Kind == PostTagKind.Product ? tag.ProductId : product?.Id, tag.WardrobeItemId, offer, inWardrobe);
        }

        return
        [
            .. posts.Select(post =>
            {
                var isAuthor = viewerId == post.AuthorId;
                var creator = creators.GetValueOrDefault(post.AuthorId);
                var isCreator = creator is { IsActive: true };
                var shown = post.Tags.Where(tag => tag.ConfirmedAt is not null).OrderBy(tag => tag.Position).Select(tag => Tag(post, tag)).ToList();
                var proposals = isAuthor
                    ? post.Tags.Where(tag => tag.ConfirmedAt is null).OrderBy(tag => tag.Position).Select(tag => new PostProposalResponse(
                        tag.Id,
                        tag.Source,
                        tag.X,
                        tag.Y,
                        tag.W,
                        tag.H,
                        tag.CategoryId is { } categoryId ? categorySlugs.GetValueOrDefault(categoryId) : null,
                        tag.Color,
                        tag.Confidence,
                        tag.WardrobeItemId,
                        tag.ProductId)).ToList()
                    : null;
                decimal? total = shown.Any(tag => tag.Offer is not null) ? shown.Sum(tag => tag.Offer?.Price ?? 0) : null;
                var matchScore = MatchScore(scoring, post.Tags.Where(tag => tag.ConfirmedAt is not null && tag.Kind == PostTagKind.Product && tag.ProductId is not null).Select(tag => products.GetValueOrDefault(tag.ProductId!.Value)));

                return new PostResponse(
                    post.Id,
                    post.Kind,
                    post.Status,
                    post.Visibility,
                    new PostAuthorResponse(post.AuthorId, authors.GetValueOrDefault(post.AuthorId) ?? string.Empty, creator?.Handle, isCreator),
                    post.Caption,
                    post.ImageFeedKey is { } feedKey
                        ? new PostImageResponse(
                            storage.GetPresignedDownloadUrl(feedKey, SocialLimits.ImageUrlLifetime),
                            post.ImagePreviewKey is { } previewKey ? storage.GetPresignedDownloadUrl(previewKey, SocialLimits.ImageUrlLifetime) : null,
                            post.ImageAspect)
                        : null,
                    shown,
                    new PostSummaryResponse(total, shown.Count, viewer is null ? null : shown.Count(tag => tag.InWardrobe is not null)),
                    post.LikeCount,
                    liked.Contains(post.Id),
                    followed.Contains(post.AuthorId),
                    matchScore,
                    isCreator ? SocialCopy.CreatorDisclosure : null,
                    isAuthor ? StatusMessage(post) : null,
                    proposals is { Count: > 0 } ? proposals : null,
                    reasons is not null && reasons.TryGetValue(post.Id, out var reason) ? reason : null,
                    post.CreatedAt,
                    post.PublishedAt);
            }),
        ];
    }

    /// <summary>Mean persona match of the products (null without a persona or without products).</summary>
    public static int? MatchScore(PersonaScoring? scoring, IEnumerable<ProductFacts?> products)
    {
        if (scoring is null)
        {
            return null;
        }

        var scores = products.OfType<ProductFacts>().Select(product => scoring.Score(product.Traits)).ToList();

        return scores.Count == 0 ? null : (int)Math.Round(scores.Average(), MidpointRounding.AwayFromZero);
    }

    /// <summary>What the author is told about a post that is not (yet) visible to others.</summary>
    public static string? StatusMessage(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);

        return post.Status switch
        {
            PostStatus.Blocked or PostStatus.Failed or PostStatus.NeedsReview => post.ModerationReason,
            PostStatus.Hidden => SocialCopy.HiddenAfterReports,
            _ => null,
        };
    }

    private static Guid? CategoryOf(PostTag tag, ProductFacts? product, WardrobeItem? item) => product?.CategoryId ?? item?.CategoryId ?? tag.CategoryId;

    private static string Describe(ProductColor color, string? slug) =>
        slug is null ? "Parça" : TurkishGrammar.Capitalize(TurkishGrammar.Phrase(color, slug));
}

/// <summary>What the response builder and the feed need to know about a tagged product.</summary>
internal sealed record ProductFacts(
    Guid Id,
    string Name,
    string BrandName,
    Guid CategoryId,
    string CategorySlug,
    ProductColor Color,
    ProductPattern Pattern,
    ProductFit Fit,
    short Formality,
    Season Seasons,
    string? ImageUrl)
{
    public ProductTraits Traits => new(Color, Formality, Fit, Seasons, Pattern, CategorySlug);
}

/// <summary>The viewer's wardrobe as the "M'si dolabında var" count sees it.</summary>
internal sealed class ViewerWardrobe(HashSet<Guid> products, HashSet<(Guid CategoryId, ProductColor Color)> pieces)
{
    public static async Task<ViewerWardrobe> LoadAsync(KombinDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && !item.IsArchived && item.Status == WardrobeItemStatus.Ready)
            .Select(item => new { item.ProductId, item.CategoryId, item.PrimaryColor })
            .ToListAsync(cancellationToken);

        return new ViewerWardrobe(
            [.. rows.Select(row => row.ProductId).OfType<Guid>()],
            [.. rows.Where(row => row.CategoryId is not null).Select(row => (row.CategoryId!.Value, row.PrimaryColor))]);
    }

    /// <summary><c>exact</c> for the viewer's own piece or a product they own, <c>similar</c> for a piece of the same category and colour.</summary>
    public string? Owns(bool ownPiece, Guid? productId, Guid? categoryId, ProductColor? color)
    {
        if (ownPiece || (productId is { } id && products.Contains(id)))
        {
            return PostResponses.Exact;
        }

        return categoryId is { } category && color is { } colour && colour != ProductColor.Other && pieces.Contains((category, colour))
            ? PostResponses.Similar
            : null;
    }
}

/// <summary>Who may see what: one place for the visibility checks of single posts and of lists.</summary>
internal static class PostAccess
{
    public static Task<bool> BlockedEitherWayAsync(KombinDbContext db, Guid? viewerId, Guid otherId, CancellationToken cancellationToken) =>
        viewerId is { } viewer && viewer != otherId
            ? db.UserBlocks.AsNoTracking().AnyAsync(block => (block.BlockerId == viewer && block.BlockedId == otherId) || (block.BlockerId == otherId && block.BlockedId == viewer), cancellationToken)
            : Task.FromResult(false);

    public static Task<bool> FollowsAsync(KombinDbContext db, Guid? viewerId, Guid authorId, CancellationToken cancellationToken) =>
        viewerId is { } viewer
            ? db.Follows.AsNoTracking().AnyAsync(follow => follow.FollowerId == viewer && follow.FolloweeId == authorId, cancellationToken)
            : Task.FromResult(false);

    /// <summary>The post with its tags when <paramref name="viewerId"/> may see it (<see cref="PostRules.CanView"/>), otherwise null.</summary>
    public static async Task<Post?> FindVisibleAsync(KombinDbContext db, Guid postId, Guid? viewerId, bool tracking, CancellationToken cancellationToken)
    {
        var query = db.Posts.Include(post => post.Tags).AsQueryable();
        var post = await (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(candidate => candidate.Id == postId, cancellationToken);
        if (post is null)
        {
            return null;
        }

        var isAuthor = viewerId == post.AuthorId;
        var facts = new PostViewFacts(
            post.Status,
            post.Visibility,
            isAuthor,
            !isAuthor && await FollowsAsync(db, viewerId, post.AuthorId, cancellationToken),
            !isAuthor && await BlockedEitherWayAsync(db, viewerId, post.AuthorId, cancellationToken));

        return PostRules.CanView(facts) ? post : null;
    }

    /// <summary>An author's posts as <paramref name="viewerId"/> may see them: live, and public or (for followers) followers-only; all of them for the author.</summary>
    public static IQueryable<Post> VisibleOf(this IQueryable<Post> posts, Guid authorId, Guid? viewerId, bool viewerFollows) =>
        viewerId == authorId
            ? posts.Where(post => post.AuthorId == authorId && post.Status == PostStatus.Live)
            : posts.Where(post => post.AuthorId == authorId
                && post.Status == PostStatus.Live
                && (post.Visibility == PostVisibility.Public || (viewerFollows && post.Visibility == PostVisibility.Followers)));
}
