using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Social.Users;

/// <summary>
/// A user as others see them. <c>Handle</c>, <c>Bio</c> and <c>Link</c> exist for creators only. <c>PostCount</c> counts the posts the caller
/// may see. <c>IsFollowing</c>: the caller follows them; <c>FollowsYou</c>: they follow the caller.
/// </summary>
internal sealed record UserProfileResponse(
    Guid Id,
    string DisplayName,
    string? Handle,
    bool IsCreator,
    string? Bio,
    string? Link,
    int FollowerCount,
    int FollowingCount,
    int PostCount,
    bool IsFollowing,
    bool FollowsYou);

internal sealed record FollowResponse(bool Following, int FollowerCount);

internal sealed record BlockedUserResponse(Guid Id, string DisplayName, DateTimeOffset BlockedAt);

/// <summary>Profiles, their posts, follows and blocks. A block in either direction makes the other user (and their posts) a 404.</summary>
internal static class UsersEndpoint
{
    public static void Map(RouteGroupBuilder users, RouteGroupBuilder me)
    {
        users.MapGet("/{id:guid}", GetProfileAsync)
            .WithName("GetUserProfile")
            .Produces<UserProfileResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapGet("/{id:guid}/posts", ListPostsAsync)
            .WithName("ListUserPosts")
            .Produces<PagedResponse<PostResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapPost("/{id:guid}/follow", FollowAsync)
            .WithName("FollowUser")
            .RequireAuthorization()
            .Produces<FollowResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapDelete("/{id:guid}/follow", UnfollowAsync)
            .WithName("UnfollowUser")
            .RequireAuthorization()
            .Produces<FollowResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapPost("/{id:guid}/block", BlockAsync)
            .WithName("BlockUser")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapDelete("/{id:guid}/block", UnblockAsync)
            .WithName("UnblockUser")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        me.MapGet("/blocks", ListBlocksAsync)
            .WithName("ListBlockedUsers")
            .Produces<IReadOnlyList<BlockedUserResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    /// <summary>The profile summary; built from the shared queries so the creator page shows the same numbers.</summary>
    public static async Task<UserProfileResponse?> BuildProfileAsync(KombinDbContext db, Guid userId, Guid? viewerId, CancellationToken cancellationToken)
    {
        // An account pending deletion is gone for everyone else: no profile, no posts, nobody to follow.
        var user = await db.Users.AsNoTracking().Where(candidate => candidate.Id == userId && candidate.DeletionRequestedAt == null).Select(candidate => new { candidate.Id, candidate.DisplayName }).SingleOrDefaultAsync(cancellationToken);
        if (user is null || await PostAccess.BlockedEitherWayAsync(db, viewerId, userId, cancellationToken))
        {
            return null;
        }

        var creator = await db.CreatorProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        var isFollowing = viewerId != userId && await PostAccess.FollowsAsync(db, viewerId, userId, cancellationToken);
        var followsYou = viewerId is { } viewer && viewer != userId && await PostAccess.FollowsAsync(db, userId, viewer, cancellationToken);

        return new UserProfileResponse(
            user.Id,
            user.DisplayName,
            creator?.Handle,
            creator is { IsActive: true },
            creator?.Bio,
            creator?.Link,
            await db.Follows.CountAsync(follow => follow.FolloweeId == userId, cancellationToken),
            await db.Follows.CountAsync(follow => follow.FollowerId == userId, cancellationToken),
            await db.Posts.AsNoTracking().VisibleOf(userId, viewerId, isFollowing).CountAsync(cancellationToken),
            isFollowing,
            followsYou);
    }

    /// <summary>The posts of <paramref name="userId"/> that <paramref name="viewerId"/> may see, newest first; null when the user is not visible.</summary>
    public static async Task<PagedResponse<PostResponse>?> ListVisiblePostsAsync(
        KombinDbContext db, IObjectStorage storage, Guid userId, Guid? viewerId, PersonaScoring? scoring, PagedRequest paging, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(user => user.Id == userId && user.DeletionRequestedAt == null, cancellationToken) || await PostAccess.BlockedEitherWayAsync(db, viewerId, userId, cancellationToken))
        {
            return null;
        }

        var follows = viewerId != userId && await PostAccess.FollowsAsync(db, viewerId, userId, cancellationToken);
        var query = db.Posts.AsNoTracking().VisibleOf(userId, viewerId, follows);
        var total = await query.CountAsync(cancellationToken);
        var posts = await query.Include(post => post.Tags)
            .OrderByDescending(post => post.PublishedAt)
            .ThenByDescending(post => post.Id)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .ToListAsync(cancellationToken);

        var items = await PostResponses.BuildAsync(db, storage, posts, viewerId, scoring, cancellationToken);

        return PagedResponse<PostResponse>.Create(items, paging, total);
    }

    private static async Task<IResult> GetProfileAsync(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken) =>
        await BuildProfileAsync(db, id, currentUser.UserIdOrNull, cancellationToken) is { } profile
            ? TypedResults.Ok(profile)
            : SocialErrors.UserNotFound.ToHttpResult();

    private static async Task<IResult> ListPostsAsync(
        Guid id,
        int? page,
        int? pageSize,
        Guid? personaId,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var viewerId = currentUser.UserIdOrNull;
        var persona = await ViewerPersona.LoadAsync(db, viewerId, personaId, clock.UtcNow, cancellationToken);
        if (persona.IsFailure)
        {
            return persona.Error.ToHttpResult();
        }

        var result = await ListVisiblePostsAsync(
            db, storage, id, viewerId, persona.Value.Scoring, new PagedRequest(page ?? 1, pageSize ?? SocialLimits.PageSize), cancellationToken);

        return result is null ? SocialErrors.UserNotFound.ToHttpResult() : TypedResults.Ok(result);
    }

    /// <summary>Idempotent. Not oneself, not across a block.</summary>
    private static async Task<IResult> FollowAsync(Guid id, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (id == userId)
        {
            return SocialErrors.CannotTargetSelf.ToHttpResult();
        }

        if (!await db.Users.AnyAsync(user => user.Id == id && user.DeletionRequestedAt == null, cancellationToken) || await PostAccess.BlockedEitherWayAsync(db, userId, id, cancellationToken))
        {
            return SocialErrors.UserNotFound.ToHttpResult();
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO social.follows (follower_id, followee_id, created_at) VALUES ({userId}, {id}, {clock.UtcNow}) ON CONFLICT DO NOTHING",
            cancellationToken);

        return TypedResults.Ok(new FollowResponse(true, await db.Follows.CountAsync(follow => follow.FolloweeId == id, cancellationToken)));
    }

    private static async Task<IResult> UnfollowAsync(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (!await db.Users.AnyAsync(user => user.Id == id, cancellationToken))
        {
            return SocialErrors.UserNotFound.ToHttpResult();
        }

        await db.Follows.Where(follow => follow.FollowerId == userId && follow.FolloweeId == id).ExecuteDeleteAsync(cancellationToken);

        return TypedResults.Ok(new FollowResponse(false, await db.Follows.CountAsync(follow => follow.FolloweeId == id, cancellationToken)));
    }

    /// <summary>Idempotent. Removes the follows in both directions in the same transaction; the blocked user is not told.</summary>
    private static async Task<IResult> BlockAsync(Guid id, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (id == userId)
        {
            return SocialErrors.CannotTargetSelf.ToHttpResult();
        }

        if (!await db.Users.AnyAsync(user => user.Id == id, cancellationToken))
        {
            return SocialErrors.UserNotFound.ToHttpResult();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO social.blocks (blocker_id, blocked_id, created_at) VALUES ({userId}, {id}, {clock.UtcNow}) ON CONFLICT DO NOTHING",
            cancellationToken);
        await db.Follows
            .Where(follow => (follow.FollowerId == userId && follow.FolloweeId == id) || (follow.FollowerId == id && follow.FolloweeId == userId))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<IResult> UnblockAsync(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        await db.UserBlocks.Where(block => block.BlockerId == currentUser.UserId && block.BlockedId == id).ExecuteDeleteAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>Whom the caller blocked (to unblock them); never who blocked the caller.</summary>
    private static async Task<IResult> ListBlocksAsync(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var blocked = await db.UserBlocks.AsNoTracking()
            .Where(block => block.BlockerId == userId)
            .OrderByDescending(block => block.CreatedAt)
            .Join(db.Users, block => block.BlockedId, user => user.Id, (block, user) => new BlockedUserResponse(user.Id, user.DisplayName, block.CreatedAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(blocked);
    }
}
