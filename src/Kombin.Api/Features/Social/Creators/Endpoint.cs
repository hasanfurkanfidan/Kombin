using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Social.Users;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Social.Creators;

/// <summary>A creator's public page: the profile, the disclosure line to show, and their first page of posts the caller may see.</summary>
internal sealed record CreatorPageResponse(UserProfileResponse Profile, string Disclosure, PagedResponse<PostResponse> Posts);

internal sealed record CreatorTopPost(Guid PostId, int Clicks, int LikeCount, string? PreviewUrl);

/// <summary>
/// The owner's numbers over the last <c>Days</c>. <c>EstimatedEarnings</c> is an estimate (its inputs are listed); <c>ConfirmedEarnings</c>
/// sums imported conversions, 0 until the import exists.
/// </summary>
internal sealed record CreatorStatsResponse(
    int Days,
    DateTimeOffset Since,
    int Clicks,
    EarningsEstimate EstimatedEarnings,
    decimal ConfirmedEarnings,
    int FollowerCount,
    IReadOnlyList<CreatorTopPost> TopPosts);

/// <summary><c>Handle</c> 3-30 of a-z, 0-9, <c>.</c> and <c>_</c> (lower-cased); <c>IsActive</c> defaults to true. Every field is replaced.</summary>
internal sealed record PutCreatorRequest(string? Handle, string? Bio, string? Link, bool? IsActive);

internal sealed record CreatorProfileResponse(Guid UserId, string Handle, string? Bio, string? Link, bool IsActive, DateTimeOffset UpdatedAt);

internal static class CreatorsEndpoint
{
    public const int DefaultDays = 30;

    public const int TopPostCount = 5;

    public static void Map(RouteGroupBuilder creators, RouteGroupBuilder me, RouteGroupBuilder internalSocial)
    {
        creators.MapGet("/{handle}", GetPageAsync)
            .WithName("GetCreator")
            .Produces<CreatorPageResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        me.MapGet("/creator/stats", GetStatsAsync)
            .WithName("GetCreatorStats")
            .Produces<CreatorStatsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        internalSocial.MapPut("/creators/{userId:guid}", PutAsync)
            .WithName("PutCreator")
            .WithValidation<PutCreatorRequest>()
            .Produces<CreatorProfileResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> GetPageAsync(string handle, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var normalized = handle.Trim().TrimStart('@').ToLowerInvariant();
        var creator = await db.CreatorProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Handle == normalized && candidate.IsActive, cancellationToken);
        var viewerId = currentUser.UserIdOrNull;
        if (creator is null || await UsersEndpoint.BuildProfileAsync(db, creator.UserId, viewerId, cancellationToken) is not { } profile)
        {
            return SocialErrors.CreatorNotFound.ToHttpResult();
        }

        var posts = await UsersEndpoint.ListVisiblePostsAsync(db, storage, creator.UserId, viewerId, null, new PagedRequest(1, SocialLimits.PageSize), cancellationToken);

        return TypedResults.Ok(new CreatorPageResponse(profile, SocialCopy.CreatorDisclosure, posts!));
    }

    /// <summary>Clicks attributed to the caller (<c>clicks.creator_id</c>), the estimate, and the posts that brought the most clicks.</summary>
    private static async Task<IResult> GetStatsAsync(
        int? days,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        IOptions<CreatorOptions> options,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (!await db.CreatorProfiles.AnyAsync(creator => creator.UserId == userId && creator.IsActive, cancellationToken))
        {
            return SocialErrors.NotACreator.ToHttpResult();
        }

        var window = Math.Clamp(days ?? DefaultDays, 1, 365);
        var since = clock.UtcNow.AddDays(-window);
        var clicks = db.Clicks.AsNoTracking().Where(click => click.CreatorId == userId && click.ClickedAt >= since);

        var total = await clicks.CountAsync(cancellationToken);
        var byPost = await clicks
            .Where(click => click.PostId != null)
            .GroupBy(click => click.PostId!.Value)
            .Select(group => new { PostId = group.Key, Clicks = group.Count() })
            .OrderByDescending(row => row.Clicks)
            .ThenBy(row => row.PostId)
            .Take(TopPostCount)
            .ToListAsync(cancellationToken);
        var postIds = byPost.Select(row => row.PostId).ToList();
        var posts = await db.Posts.AsNoTracking()
            .Where(post => postIds.Contains(post.Id) && post.Status != PostStatus.Deleted)
            .Select(post => new { post.Id, post.LikeCount, post.ImagePreviewKey })
            .ToDictionaryAsync(post => post.Id, cancellationToken);

        var confirmed = await db.ReferralEvents.AsNoTracking()
            .Where(referral => referral.CreatorId == userId && referral.Kind == ReferralKind.Conversion && referral.OccurredAt >= since)
            .SumAsync(referral => referral.CreatorAmount ?? 0, cancellationToken);

        return TypedResults.Ok(new CreatorStatsResponse(
            window,
            since,
            total,
            CreatorEarnings.Estimate(total, options.Value),
            confirmed,
            await db.Follows.CountAsync(follow => follow.FolloweeId == userId, cancellationToken),
            [
                .. byPost.Where(row => posts.ContainsKey(row.PostId)).Select(row => new CreatorTopPost(
                    row.PostId,
                    row.Clicks,
                    posts[row.PostId].LikeCount,
                    posts[row.PostId].ImagePreviewKey is { } key ? storage.GetPresignedDownloadUrl(key, SocialLimits.ImageUrlLifetime) : null)),
            ]));
    }

    /// <summary>The founder's invitation (internal key): makes a user a creator or updates their page. No self-signup in v1.</summary>
    private static async Task<IResult> PutAsync(Guid userId, PutCreatorRequest request, KombinDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return SocialErrors.UserNotFound.ToHttpResult();
        }

        var handle = request.Handle!.Trim().TrimStart('@').ToLowerInvariant();
        if (await db.CreatorProfiles.AnyAsync(creator => creator.Handle == handle && creator.UserId != userId, cancellationToken))
        {
            return SocialErrors.HandleTaken.ToHttpResult();
        }

        var creator = await db.CreatorProfiles.SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (creator is null)
        {
            creator = new CreatorProfile { UserId = userId };
            db.CreatorProfiles.Add(creator);
        }

        creator.Handle = handle;
        creator.Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio.Trim();
        creator.Link = string.IsNullOrWhiteSpace(request.Link) ? null : request.Link.Trim();
        creator.IsActive = request.IsActive ?? true;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new CreatorProfileResponse(creator.UserId, creator.Handle, creator.Bio, creator.Link, creator.IsActive, creator.UpdatedAt));
    }
}
