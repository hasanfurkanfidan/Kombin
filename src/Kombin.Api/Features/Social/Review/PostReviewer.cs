using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.Review;

/// <summary>
/// A reviewer's decision on a hidden or borderline post: the one place that moves a post out of review, shared by
/// <c>POST /internal/social/posts/{id}/review</c> and the admin panel so neither can drift from the other.
/// </summary>
internal sealed class PostReviewer(KombinDbContext db, IClock clock)
{
    /// <summary>
    /// Approve: live again (or back to draft if the author never confirmed it); the report count restarts, so three new reporters are needed
    /// to hide it again. Remove: blocked, with <paramref name="reason"/> (cut to the column's length) or the default copy as what the author sees.
    /// Keep: nothing changes except that the post moves to the back of the queue (its <c>UpdatedAt</c>, which the queue sorts by), so a
    /// post the reviewer is not ready to decide on does not block the others; the report count and <c>ReviewedAt</c> stay, as they count reports.
    /// </summary>
    public async Task<Result<ReviewPostResponse>> ReviewAsync(Guid postId, ReviewDecision decision, string? reason, CancellationToken cancellationToken)
    {
        var post = await db.Posts.SingleOrDefaultAsync(candidate => candidate.Id == postId, cancellationToken);
        if (post is null || post.Status == PostStatus.Deleted)
        {
            return SocialErrors.PostNotFound;
        }

        if (!PostRules.IsReviewable(post.Status))
        {
            return SocialErrors.NotReviewable;
        }

        if (decision == ReviewDecision.Keep)
        {
            db.Entry(post).Property(candidate => candidate.UpdatedAt).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);

            return new ReviewPostResponse(post.Id, post.Status);
        }

        var now = clock.UtcNow;
        post.Status = PostRules.StatusAfterReview(decision, post.ConfirmedAt is not null);
        post.ReviewedAt = now;
        post.ReportCount = 0;
        post.HiddenAt = null;
        post.ModerationReason = decision == ReviewDecision.Remove ? RemovalReason(reason) : null;
        if (post.Status == PostStatus.Live)
        {
            post.PublishedAt ??= now;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new ReviewPostResponse(post.Id, post.Status);
    }

    /// <summary>What the author reads: the reviewer's text when there is one, else the standard copy.</summary>
    internal static string RemovalReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return SocialCopy.RemovedAfterReview;
        }

        return trimmed.Length <= Post.MaxModerationReasonLength ? trimmed : trimmed[..Post.MaxModerationReasonLength];
    }
}
