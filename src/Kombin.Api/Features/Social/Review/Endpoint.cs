using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.Review;

/// <summary>A post waiting for a human: why (moderation labels or reports), and a 1-hour link to its preview.</summary>
internal sealed record ReviewItem(
    Guid PostId,
    Guid AuthorId,
    PostKind Kind,
    PostStatus Status,
    string? ModerationJson,
    IReadOnlyDictionary<string, int> ReportReasons,
    int ReportCount,
    string? PreviewUrl,
    string? Caption,
    DateTimeOffset UpdatedAt);

/// <summary><c>Decision</c>: <c>Approve</c> or <c>Remove</c>.</summary>
internal sealed record ReviewPostRequest(string? Decision);

internal sealed record ReviewPostResponse(Guid PostId, PostStatus Status);

/// <summary>The founder's moderation tools behind the internal key (no admin UI in v1).</summary>
internal static class ReviewEndpoint
{
    public const int QueueLimit = 50;

    public static void Map(RouteGroupBuilder internalSocial)
    {
        internalSocial.MapGet("/review-queue", ListAsync)
            .WithName("GetReviewQueue")
            .Produces<IReadOnlyList<ReviewItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        internalSocial.MapPost("/posts/{id:guid}/review", ReviewAsync)
            .WithName("ReviewPost")
            .WithValidation<ReviewPostRequest>()
            .Produces<ReviewPostResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>Hidden (reported) and borderline posts, oldest first.</summary>
    private static async Task<IResult> ListAsync(KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var posts = await db.Posts.AsNoTracking()
            .Where(post => post.Status == PostStatus.Hidden || post.Status == PostStatus.NeedsReview)
            .OrderBy(post => post.UpdatedAt)
            .Take(QueueLimit)
            .ToListAsync(cancellationToken);
        var ids = posts.Select(post => post.Id).ToList();
        var reasons = (await db.PostReports.AsNoTracking()
                .Where(report => ids.Contains(report.PostId))
                .GroupBy(report => new { report.PostId, report.Reason })
                .Select(group => new { group.Key.PostId, group.Key.Reason, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.PostId);

        return TypedResults.Ok(posts.Select(post => new ReviewItem(
            post.Id,
            post.AuthorId,
            post.Kind,
            post.Status,
            post.ModerationJson,
            reasons[post.Id].ToDictionary(row => row.Reason.ToString(), row => row.Count),
            post.ReportCount,
            post.ImagePreviewKey is { } key ? storage.GetPresignedDownloadUrl(key, SocialLimits.ImageUrlLifetime) : null,
            post.Caption,
            post.UpdatedAt)).ToList());
    }

    /// <summary>
    /// Approve: live again (or back to draft if the author never confirmed it); the report count restarts, so three new reporters are needed
    /// to hide it again. Remove: blocked, with the reason the author sees.
    /// </summary>
    private static async Task<IResult> ReviewAsync(Guid id, ReviewPostRequest request, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var post = await db.Posts.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (post is null || post.Status == PostStatus.Deleted)
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        if (!PostRules.IsReviewable(post.Status))
        {
            return SocialErrors.NotReviewable.ToHttpResult();
        }

        var now = clock.UtcNow;
        var decision = EnumNames.TryParse<ReviewDecision>(request.Decision, out var parsed) ? parsed : ReviewDecision.Remove;
        post.Status = PostRules.StatusAfterReview(decision, post.ConfirmedAt is not null);
        post.ReviewedAt = now;
        post.ReportCount = 0;
        post.HiddenAt = null;
        post.ModerationReason = decision == ReviewDecision.Remove ? SocialCopy.RemovedAfterReview : null;
        if (post.Status == PostStatus.Live)
        {
            post.PublishedAt ??= now;
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new ReviewPostResponse(post.Id, post.Status));
    }
}
