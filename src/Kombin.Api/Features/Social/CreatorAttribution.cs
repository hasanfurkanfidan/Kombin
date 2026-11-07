using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social;

/// <summary>What a buy click owes to a post: the post and tag it came from, and the creator who earns from it (null for a non-creator).</summary>
internal sealed record Attribution(Guid PostId, Guid TagId, Guid? CreatorId);

/// <summary>
/// Used by <c>/go</c> (the one place a buy click is recorded) to attribute a click carrying <c>post=</c>. The post must be live and not
/// private, and one of its confirmed tags must be the clicked product (a product tag, or a wardrobe tag whose piece came from the catalog):
/// anything else is ignored, so a forged parameter attributes nothing. The creator is the author when they are an active creator and not
/// the clicker (no self-referral).
/// </summary>
internal static class CreatorAttribution
{
    public static async Task<Attribution?> ResolveAsync(KombinDbContext db, string? post, Guid productId, Guid? viewerId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(post, out var postId) || postId == Guid.Empty)
        {
            return null;
        }

        var match = await db.PostTags.AsNoTracking()
            .Where(tag => tag.PostId == postId
                && tag.ConfirmedAt != null
                && (tag.ProductId == productId || db.WardrobeItems.Any(item => item.Id == tag.WardrobeItemId && item.ProductId == productId)))
            .Join(
                db.Posts.Where(candidate => candidate.Status == PostStatus.Live && candidate.Visibility != PostVisibility.Private),
                tag => tag.PostId,
                candidate => candidate.Id,
                (tag, candidate) => new { TagId = tag.Id, tag.Position, candidate.AuthorId })
            .OrderBy(row => row.Position)
            .FirstOrDefaultAsync(cancellationToken);
        if (match is null)
        {
            return null;
        }

        var isCreator = viewerId != match.AuthorId
            && await db.CreatorProfiles.AnyAsync(creator => creator.UserId == match.AuthorId && creator.IsActive, cancellationToken);

        return new Attribution(postId, match.TagId, isCreator ? match.AuthorId : null);
    }

    /// <summary>The append-only referral row of a creator's click, with the share in force now.</summary>
    public static ReferralEvent ClickEvent(Attribution attribution, Click click, decimal sharePercent)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(click);

        return new ReferralEvent
        {
            Kind = ReferralKind.Click,
            CreatorId = attribution.CreatorId ?? throw new ArgumentException("Only a creator's click has a referral event.", nameof(attribution)),
            PostId = attribution.PostId,
            PostTagId = attribution.TagId,
            ClickId = click.Id,
            ProductId = click.ProductId,
            OfferId = click.OfferId,
            SellerId = click.SellerId,
            ViewerUserId = click.UserId,
            SharePercent = sharePercent,
            OccurredAt = click.ClickedAt,
        };
    }
}
