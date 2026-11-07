using System.Runtime.CompilerServices;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.GetFeed;

/// <summary><c>Mode</c> is <c>forYou</c> (default) or <c>following</c>; <c>PersonaId</c> follows the catalog convention.</summary>
internal sealed record FeedQuery(string? Mode, int? Page, Guid? PersonaId);

/// <summary>A page of 20 over at most ~230 ranked posts; <c>HasMore</c> says whether the next page has any.</summary>
internal sealed record FeedResponse(string Mode, IReadOnlyList<PostResponse> Items, int Page, int PageSize, bool HasMore);

/// <summary>One candidate row of the SQL queries below.</summary>
internal sealed class FeedRow
{
    public Guid PostId { get; init; }

    public Guid AuthorId { get; init; }

    public DateTimeOffset PublishedAt { get; init; }

    public int LikeCount { get; init; }

    public bool IsCreator { get; init; }
}

internal static class GetFeedEndpoint
{
    public const string ForYou = "forYou";

    public const string Following = "following";

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/feed", Handle)
            .WithName("GetFeed")
            .WithValidation<FeedQuery>()
            .Produces<FeedResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// <c>forYou</c>: followed authors' posts of the last 72 h first (newest first), then the ranked "sana göre" posts (<see cref="FeedRanking"/>).
    /// <c>following</c>: the newest posts of followed authors (sign-in required). Anonymous callers get public posts only. In both modes no
    /// two posts of one author are adjacent, and blocked users (either way) never appear.
    /// </summary>
    private static async Task<IResult> Handle(
        [AsParameters] FeedQuery query, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IClock clock, CancellationToken cancellationToken)
    {
        var viewerId = currentUser.UserIdOrNull;
        var mode = string.Equals(query.Mode, Following, StringComparison.OrdinalIgnoreCase) ? Following : ForYou;
        if (mode == Following && viewerId is null)
        {
            return SocialErrors.FollowingRequiresAuth.ToHttpResult();
        }

        var now = clock.UtcNow;
        var persona = await ViewerPersona.LoadAsync(db, viewerId, query.PersonaId, now, cancellationToken);
        if (persona.IsFailure)
        {
            return persona.Error.ToHttpResult();
        }

        var viewer = persona.Value;
        List<FeedCandidate> ordered;

        if (mode == Following)
        {
            var rows = await FollowingRowsAsync(db, viewerId!.Value, now - TimeSpan.FromDays(3650), FeedRanking.CandidateLimit, cancellationToken);
            ordered = FeedRanking.RankFollowing(rows.Select(row => Candidate(row, null, FeedReason.Following)));
        }
        else
        {
            var lead = viewerId is { } follower
                ? FeedRanking.RankFollowing((await FollowingRowsAsync(db, follower, now - FeedRanking.FollowingFreshness, FeedRanking.FollowingLeadLimit, cancellationToken))
                    .Select(row => Candidate(row, null, FeedReason.Following)))
                : [];

            var rows = await ForYouRowsAsync(db, viewerId ?? Guid.Empty, viewer.Audience, now, cancellationToken);
            var matches = await MatchScoresAsync(db, viewer.Scoring, [.. rows.Select(row => row.PostId)], cancellationToken);
            var ranked = FeedRanking.RankForYou(
                rows.Select(row => Candidate(row, matches.TryGetValue(row.PostId, out var match) ? match : null, FeedReason.ForYou)),
                now,
                viewer.Scoring is not null && viewer.Learned);

            var leading = lead.Select(candidate => candidate.PostId).ToHashSet();
            ordered = [.. lead, .. ranked.Where(candidate => !leading.Contains(candidate.PostId))];
        }

        var feed = FeedRanking.SeparateAuthors(ordered, candidate => candidate.AuthorId);
        var page = Math.Max(1, query.Page ?? 1);
        var slice = feed.Skip((page - 1) * SocialLimits.PageSize).Take(SocialLimits.PageSize).ToList();
        var ids = slice.Select(candidate => candidate.PostId).ToList();

        var posts = (await db.Posts.AsNoTracking().Include(post => post.Tags).Where(post => ids.Contains(post.Id)).ToListAsync(cancellationToken))
            .OrderBy(post => ids.IndexOf(post.Id))
            .ToList();
        var items = await PostResponses.BuildAsync(
            db, storage, posts, viewerId, viewer.Scoring, cancellationToken, slice.ToDictionary(candidate => candidate.PostId, candidate => candidate.Reason));

        return TypedResults.Ok(new FeedResponse(mode, items, page, SocialLimits.PageSize, feed.Count > page * SocialLimits.PageSize));
    }

    private static FeedCandidate Candidate(FeedRow row, int? match, FeedReason reason) =>
        new(row.PostId, row.AuthorId, row.PublishedAt, row.LikeCount, row.IsCreator, match, reason);

    /// <summary>Live public or followers-only posts of the authors <paramref name="viewerId"/> follows, newest first.</summary>
    private static Task<List<FeedRow>> FollowingRowsAsync(KombinDbContext db, Guid viewerId, DateTimeOffset since, int limit, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<FeedRow>($"""
            SELECT p.id AS post_id, p.author_id, p.published_at, p.like_count, (c.user_id IS NOT NULL) AS is_creator
            FROM social.posts p
            JOIN social.follows f ON f.followee_id = p.author_id AND f.follower_id = {viewerId}
            LEFT JOIN social.creator_profiles c ON c.user_id = p.author_id AND c.is_active
            WHERE p.status = 'Live' AND p.visibility IN ('Public', 'Followers') AND p.published_at >= {since}
              AND NOT EXISTS (SELECT 1 FROM social.blocks b
                              WHERE (b.blocker_id = {viewerId} AND b.blocked_id = p.author_id) OR (b.blocker_id = p.author_id AND b.blocked_id = {viewerId}))
            ORDER BY p.published_at DESC, p.id DESC
            LIMIT {limit}
            """).ToListAsync(cancellationToken);

    /// <summary>
    /// The "sana göre" candidates: live public posts of the window by authors the viewer does not follow, not their own, not blocked either
    /// way, for the viewer's audience (or unisex), best <see cref="FeedRanking.CandidateLimit"/> by <see cref="FeedRanking.BaseSql"/>.
    /// An anonymous viewer is <see cref="Guid.Empty"/>, which follows and blocks nobody.
    /// </summary>
    private static Task<List<FeedRow>> ForYouRowsAsync(KombinDbContext db, Guid viewerId, Audience audience, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sql = FormattableStringFactory.Create(
            $$"""
            SELECT p.id AS post_id, p.author_id, p.published_at, p.like_count, (c.user_id IS NOT NULL) AS is_creator
            FROM social.posts p
            LEFT JOIN social.creator_profiles c ON c.user_id = p.author_id AND c.is_active
            WHERE p.status = 'Live' AND p.visibility = 'Public' AND p.published_at >= {1}
              AND p.author_id <> {2}
              AND ({3} = 'Unisex' OR p.audience = {3} OR p.audience = 'Unisex')
              AND NOT EXISTS (SELECT 1 FROM social.follows f WHERE f.follower_id = {2} AND f.followee_id = p.author_id)
              AND NOT EXISTS (SELECT 1 FROM social.blocks b
                              WHERE (b.blocker_id = {2} AND b.blocked_id = p.author_id) OR (b.blocker_id = p.author_id AND b.blocked_id = {2}))
            ORDER BY {{FeedRanking.BaseSql}} DESC, p.published_at DESC, p.id
            LIMIT {4}
            """,
            now,
            now - FeedRanking.Window,
            viewerId,
            audience.ToString(),
            FeedRanking.CandidateLimit);

        return db.Database.SqlQuery<FeedRow>(sql).ToListAsync(cancellationToken);
    }

    /// <summary>Per post, the mean persona match of its confirmed product tags; empty without a persona.</summary>
    private static async Task<Dictionary<Guid, int>> MatchScoresAsync(KombinDbContext db, PersonaScoring? scoring, List<Guid> postIds, CancellationToken cancellationToken)
    {
        if (scoring is null || postIds.Count == 0)
        {
            return [];
        }

        var rows = await db.PostTags.AsNoTracking()
            .Where(tag => postIds.Contains(tag.PostId) && tag.ConfirmedAt != null && tag.Kind == PostTagKind.Product)
            .Join(db.Products, tag => tag.ProductId, product => product.Id, (tag, product) => new
            {
                tag.PostId,
                product.PrimaryColor,
                product.Formality,
                product.Fit,
                product.Seasons,
                product.Pattern,
                CategorySlug = product.Category.Slug,
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.PostId)
            .ToDictionary(
                group => group.Key,
                group => (int)Math.Round(
                    group.Average(row => scoring.Score(new ProductTraits(row.PrimaryColor, row.Formality, row.Fit, row.Seasons, row.Pattern, row.CategorySlug))),
                    MidpointRounding.AwayFromZero));
    }
}
