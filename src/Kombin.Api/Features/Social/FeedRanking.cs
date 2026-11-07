namespace Kombin.Api.Features.Social;

/// <summary>Why a post is in the feed: its author is followed, or it was ranked for the viewer.</summary>
internal enum FeedReason
{
    Following,
    ForYou,
}

/// <summary>
/// A post the SQL candidate query returned. <c>MatchScore</c> is the mean persona match of its product tags for the viewer's active persona
/// (0-100), null without a persona or without product tags.
/// </summary>
internal sealed record FeedCandidate(Guid PostId, Guid AuthorId, DateTimeOffset PublishedAt, int LikeCount, bool AuthorIsCreator, int? MatchScore, FeedReason Reason);

/// <summary>
/// The feed's ranking, pure and explainable. The SQL candidate query orders the 14-day window by <see cref="Base"/> (the same formula,
/// written in <see cref="BaseSql"/>) and keeps the best <see cref="CandidateLimit"/>; the rest happens here:
/// <code>
/// recency    = exp(−ageHours / 48)                   newest 1.0, a day 0.61, two days 0.37, a week 0.03
/// popularity = 1 + ln(1 + likes) / 4                 0 likes 1.0, 10 likes 1.6, 100 likes 2.15, 1000 likes 2.73
/// creator    = 1.3 for an active creator, else 1     the boost never exceeds 1.3×
/// base       = recency × popularity × creator
/// match      = persona match of the product tags     a viewer with a persona never sees posts below 60 (or without product tags)
/// score      = base × match / 100                    (base alone without a persona)
/// </code>
/// Ties go to the newest post, then the post ID. Finally <see cref="SeparateAuthors{T}"/> makes sure no two posts of one author are adjacent.
/// Change a constant here, in <see cref="BaseSql"/> and in <c>docs/design/social.md</c> together.
/// </summary>
internal static class FeedRanking
{
    public const double RecencyHours = 48;

    public const double LikeDivisor = 4;

    public const double CreatorBoost = 1.3;

    public const int MinMatchScore = 60;

    /// <summary>How many posts the SQL query hands to the in-memory rerank, per section.</summary>
    public const int CandidateLimit = 200;

    /// <summary>Followed authors' posts newer than this lead the for-you feed.</summary>
    public static readonly TimeSpan FollowingFreshness = TimeSpan.FromHours(72);

    /// <summary>At most this many followed posts lead the for-you feed before the ranked ones.</summary>
    public const int FollowingLeadLimit = 30;

    /// <summary>The for-you window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    /// <summary>
    /// <see cref="Base"/> in SQL, for the candidate query (<c>p</c> is <c>social.posts</c>, <c>c</c> the joined active creator profile, <c>{0}</c>
    /// the current instant).
    /// </summary>
    public const string BaseSql =
        "exp(-extract(epoch FROM ({0} - p.published_at)) / 3600.0 / 48.0) * (1 + ln(1 + p.like_count) / 4.0) * (CASE WHEN c.user_id IS NULL THEN 1.0 ELSE 1.3 END)";

    public static double Recency(DateTimeOffset publishedAt, DateTimeOffset now) => Math.Exp(-Math.Max(0, (now - publishedAt).TotalHours) / RecencyHours);

    public static double Popularity(int likes) => 1 + (Math.Log(1 + Math.Max(0, likes)) / LikeDivisor);

    public static double Creator(bool isCreator) => isCreator ? CreatorBoost : 1;

    public static double Base(FeedCandidate candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return Recency(candidate.PublishedAt, now) * Popularity(candidate.LikeCount) * Creator(candidate.AuthorIsCreator);
    }

    public static double Score(FeedCandidate candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return Base(candidate, now) * (candidate.MatchScore is { } match ? match / 100.0 : 1);
    }

    /// <summary>The "sana göre" part: with a persona, posts below <see cref="MinMatchScore"/> are dropped; best first.</summary>
    public static List<FeedCandidate> RankForYou(IEnumerable<FeedCandidate> candidates, DateTimeOffset now, bool viewerHasPersona) =>
    [
        .. candidates
            .Where(candidate => !viewerHasPersona || candidate.MatchScore >= MinMatchScore)
            .OrderByDescending(candidate => Score(candidate, now))
            .ThenByDescending(candidate => candidate.PublishedAt)
            .ThenBy(candidate => candidate.PostId),
    ];

    /// <summary>Followed posts: newest first.</summary>
    public static List<FeedCandidate> RankFollowing(IEnumerable<FeedCandidate> candidates) =>
        [.. candidates.OrderByDescending(candidate => candidate.PublishedAt).ThenByDescending(candidate => candidate.PostId)];

    /// <summary>
    /// Never two posts by the same author next to each other: at each position, the first remaining post (in the given order) whose author
    /// differs from the previous one. Stable, so the order is kept wherever the rule allows. Posts that cannot be placed (only one author
    /// left) are dropped from this list.
    /// </summary>
    public static List<T> SeparateAuthors<T>(IReadOnlyList<T> ordered, Func<T, Guid> authorOf)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(authorOf);

        var remaining = new List<T>(ordered);
        var result = new List<T>(ordered.Count);
        Guid? previous = null;

        while (remaining.Count > 0)
        {
            var index = remaining.FindIndex(item => authorOf(item) != previous);
            if (index < 0)
            {
                break;
            }

            var next = remaining[index];
            remaining.RemoveAt(index);
            result.Add(next);
            previous = authorOf(next);
        }

        return result;
    }
}
