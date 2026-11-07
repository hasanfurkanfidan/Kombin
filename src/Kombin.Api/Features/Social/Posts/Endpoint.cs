using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.Posts;

internal sealed record LikeResponse(bool Liked, int LikeCount);

/// <summary>Reading, deleting and liking single posts, and the author's own list.</summary>
internal static class PostsEndpoint
{
    public static void MapPosts(RouteGroupBuilder posts)
    {
        // Public: anonymous callers see public posts; a token adds what the user may see.
        posts.MapGet("/{id:guid}", GetAsync)
            .WithName("GetPost")
            .Produces<PostResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        posts.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeletePost")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        posts.MapPost("/{id:guid}/like", LikeAsync)
            .WithName("LikePost")
            .RequireAuthorization()
            .Produces<LikeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        posts.MapDelete("/{id:guid}/like", UnlikeAsync)
            .WithName("UnlikePost")
            .RequireAuthorization()
            .Produces<LikeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    public static void MapMine(RouteGroupBuilder me) =>
        me.MapGet("/posts", ListMineAsync)
            .WithName("ListMyPosts")
            .Produces<PagedResponse<PostResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>A post the caller may see (<see cref="PostRules.CanView"/>); anything else is 404. <c>personaId</c> follows the catalog convention.</summary>
    private static async Task<IResult> GetAsync(
        Guid id, Guid? personaId, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IClock clock, CancellationToken cancellationToken)
    {
        var viewerId = currentUser.UserIdOrNull;
        var persona = await ViewerPersona.LoadAsync(db, viewerId, personaId, clock.UtcNow, cancellationToken);
        if (persona.IsFailure)
        {
            return persona.Error.ToHttpResult();
        }

        var post = await PostAccess.FindVisibleAsync(db, id, viewerId, tracking: false, cancellationToken);
        if (post is null)
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        var response = await PostResponses.BuildAsync(db, storage, [post], viewerId, persona.Value.Scoring, cancellationToken);

        return TypedResults.Ok(response[0]);
    }

    /// <summary>Every post of the caller that is not deleted, whatever its status, newest first.</summary>
    private static async Task<IResult> ListMineAsync(
        int? page, int? pageSize, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var paging = new PagedRequest(page ?? 1, pageSize ?? SocialLimits.PageSize);
        var query = db.Posts.AsNoTracking().Where(post => post.AuthorId == currentUser.UserId && post.Status != PostStatus.Deleted);
        var total = await query.CountAsync(cancellationToken);
        var posts = await query.Include(post => post.Tags)
            .OrderByDescending(post => post.CreatedAt)
            .ThenByDescending(post => post.Id)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .ToListAsync(cancellationToken);

        var items = await PostResponses.BuildAsync(db, storage, posts, currentUser.UserId, null, cancellationToken);

        return TypedResults.Ok(PagedResponse<PostResponse>.Create(items, paging, total));
    }

    /// <summary>
    /// The post disappears at once (a tombstone nobody can read); <c>social-purge</c> deletes its images, tags, likes and reports within the
    /// hour. Deleting again is fine.
    /// </summary>
    private static async Task<IResult> DeleteAsync(Guid id, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var post = await db.Posts.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.AuthorId == currentUser.UserId, cancellationToken);
        if (post is null)
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        if (post.Status != PostStatus.Deleted)
        {
            post.Status = PostStatus.Deleted;
            post.DeletedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Idempotent. The count is recomputed from the rows, never incremented, so concurrent likes cannot drift it.</summary>
    private static async Task<IResult> LikeAsync(Guid id, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (await PostAccess.FindVisibleAsync(db, id, userId, tracking: false, cancellationToken) is not { Status: PostStatus.Live })
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO social.likes (post_id, user_id, created_at) VALUES ({id}, {userId}, {clock.UtcNow}) ON CONFLICT DO NOTHING",
            cancellationToken);

        return TypedResults.Ok(new LikeResponse(true, await RecountAsync(db, id, cancellationToken)));
    }

    private static async Task<IResult> UnlikeAsync(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (await PostAccess.FindVisibleAsync(db, id, userId, tracking: false, cancellationToken) is null)
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        await db.PostLikes.Where(like => like.PostId == id && like.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        return TypedResults.Ok(new LikeResponse(false, await RecountAsync(db, id, cancellationToken)));
    }

    private static async Task<int> RecountAsync(KombinDbContext db, Guid postId, CancellationToken cancellationToken)
    {
        await db.Posts.Where(post => post.Id == postId)
            .ExecuteUpdateAsync(update => update.SetProperty(post => post.LikeCount, post => db.PostLikes.Count(like => like.PostId == post.Id)), cancellationToken);

        return await db.Posts.AsNoTracking().Where(post => post.Id == postId).Select(post => post.LikeCount).SingleAsync(cancellationToken);
    }
}
