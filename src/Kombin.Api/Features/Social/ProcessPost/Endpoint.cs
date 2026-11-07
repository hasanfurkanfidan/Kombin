using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Social.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.ProcessPost;

internal sealed record ProcessPostResponse(Guid PostId, PostStatus Status);

internal static class ProcessPostEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/process", Handle)
            .WithName("ProcessPost")
            .Produces<ProcessPostResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Enqueues <c>post-process</c> (safety filter, renditions, tag proposals); poll <c>GET /posts/{id}</c>. Idempotent like the wardrobe's:
    /// a post being processed is not enqueued again for a while, a processed one is left alone, a failed one is processed again.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IBackgroundJobClient jobs, IClock clock, CancellationToken cancellationToken)
    {
        var post = await db.Posts.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.AuthorId == currentUser.UserId && candidate.Status != PostStatus.Deleted, cancellationToken);
        if (post is null)
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        var accepted = TypedResults.Accepted($"/api/v1/posts/{post.Id}", new ProcessPostResponse(post.Id, post.Status));
        if (post.Status is not (PostStatus.Processing or PostStatus.Failed)
            || (post.Status == PostStatus.Processing && post.ProcessRequestedAt > now - SocialLimits.ReprocessAfter))
        {
            return accepted;
        }

        if (post.Kind == PostKind.Photo)
        {
            var size = post.ImageOriginalKey is null ? null : await storage.GetSizeAsync(post.ImageOriginalKey, cancellationToken);
            if (size is null)
            {
                return SocialErrors.UploadMissing.ToHttpResult();
            }

            if (size > SocialLimits.MaxImageBytes)
            {
                return SocialErrors.ImageTooLarge.ToHttpResult();
            }
        }

        post.Status = PostStatus.Processing;
        post.FailureReason = null;
        post.ModerationReason = null;
        post.ProcessingAttempts = 0;
        post.ProcessRequestedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        jobs.Enqueue<PostProcessJob>(job => job.RunAsync(post.Id, CancellationToken.None));

        return TypedResults.Accepted($"/api/v1/posts/{post.Id}", new ProcessPostResponse(post.Id, post.Status));
    }
}
