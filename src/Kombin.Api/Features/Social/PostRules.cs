using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social;

/// <summary>What decides whether a viewer may see a post.</summary>
internal readonly record struct PostViewFacts(PostStatus Status, PostVisibility Visibility, bool IsAuthor, bool ViewerFollowsAuthor, bool BlockedEitherWay);

internal enum ReviewDecision
{
    Approve,
    Remove,
}

/// <summary>The social feature's rules, pure (unit-tested in <c>SocialRulesTests</c>).</summary>
internal static class PostRules
{
    /// <summary>The third distinct reporter hides a live post until a reviewer decides.</summary>
    public const int HideAtReports = 3;

    /// <summary>
    /// The author sees every post of theirs that is not deleted. Anyone else needs a live post, no block between them and the author, and
    /// the visibility: public for everyone (anonymous included), followers for followers, private for nobody.
    /// </summary>
    public static bool CanView(PostViewFacts facts)
    {
        if (facts.IsAuthor)
        {
            return facts.Status != PostStatus.Deleted;
        }

        if (facts.BlockedEitherWay || facts.Status != PostStatus.Live)
        {
            return false;
        }

        return facts.Visibility switch
        {
            PostVisibility.Public => true,
            PostVisibility.Followers => facts.ViewerFollowsAuthor,
            _ => false,
        };
    }

    /// <summary>Several images (an outfit post's pieces): the strictest verdict wins.</summary>
    public static ModerationVerdict Combine(IEnumerable<ModerationVerdict> verdicts) =>
        verdicts.DefaultIfEmpty(ModerationVerdict.Allow).Max();

    /// <summary>Where moderation leaves a post: safe ones wait for the author's confirmation, borderline ones for a reviewer.</summary>
    public static PostStatus StatusAfterModeration(ModerationVerdict verdict) => verdict switch
    {
        ModerationVerdict.Allow => PostStatus.Draft,
        ModerationVerdict.Review => PostStatus.NeedsReview,
        _ => PostStatus.Blocked,
    };

    /// <summary>The author's confirmation: a draft goes live; a post under review stays there (confirmed) until a reviewer approves it.</summary>
    public static PostStatus StatusAfterConfirmation(PostStatus status) => status == PostStatus.Draft ? PostStatus.Live : status;

    public static bool ShouldHide(PostStatus status, int reportCount) => status == PostStatus.Live && reportCount >= HideAtReports;

    /// <summary>A reviewer's decision on a hidden or borderline post: approved posts go live if the author confirmed them (else back to draft).</summary>
    public static PostStatus StatusAfterReview(ReviewDecision decision, bool confirmed) => decision switch
    {
        ReviewDecision.Approve => confirmed ? PostStatus.Live : PostStatus.Draft,
        _ => PostStatus.Blocked,
    };

    /// <summary>Statuses a reviewer can act on.</summary>
    public static bool IsReviewable(PostStatus status) => status is PostStatus.Hidden or PostStatus.NeedsReview;

    /// <summary>Whether the author may still publish (or re-publish with new tags) the post.</summary>
    public static bool CanPublish(PostStatus status) => status is PostStatus.Draft or PostStatus.NeedsReview or PostStatus.Live;
}

/// <summary>
/// Retention of the purge job (<c>social-purge</c>, hourly). A deleted post's objects and rows go at the next run, so always within 24 h;
/// the others are abandoned or refused posts nobody will see.
/// </summary>
internal static class PurgeRules
{
    /// <summary>An upload never processed.</summary>
    public static readonly TimeSpan AbandonedUpload = TimeSpan.FromDays(1);

    /// <summary>A draft the author never published.</summary>
    public static readonly TimeSpan StaleDraft = TimeSpan.FromDays(7);

    /// <summary>A blocked post, kept this long for questions about the decision.</summary>
    public static readonly TimeSpan BlockedRetention = TimeSpan.FromDays(30);

    public static bool IsDue(PostStatus status, DateTimeOffset? processRequestedAt, DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset now) => status switch
    {
        PostStatus.Deleted => true,
        PostStatus.Processing => processRequestedAt is null && createdAt < now - AbandonedUpload,
        PostStatus.Failed => updatedAt < now - AbandonedUpload,
        PostStatus.Draft => updatedAt < now - StaleDraft,
        PostStatus.Blocked => updatedAt < now - BlockedRetention,
        _ => false,
    };
}

/// <summary>An outfit post's collage: one cell per piece in a two-column grid, layers and tops first.</summary>
internal static class CollageLayout
{
    private const double Gap = 0.02;

    private static readonly OutfitSlot[] Order = [OutfitSlot.Outerwear, OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Shoes, OutfitSlot.Accessory];

    /// <summary>The pieces in collage order with their boxes (fractions of the square canvas).</summary>
    public static List<(T Piece, double X, double Y, double W, double H)> Arrange<T>(IReadOnlyList<T> pieces, Func<T, OutfitSlot> slotOf)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(slotOf);

        var ordered = pieces.OrderBy(piece => Array.IndexOf(Order, slotOf(piece))).ToList();
        var rows = Math.Max(1, (int)Math.Ceiling(ordered.Count / 2.0));
        var cellHeight = 1.0 / rows;

        return
        [
            .. ordered.Select((piece, index) => (
                piece,
                Math.Round(((index % 2) * 0.5) + Gap, 4),
                Math.Round(((index / 2) * cellHeight) + Gap, 4),
                Math.Round(0.5 - (2 * Gap), 4),
                Math.Round(cellHeight - (2 * Gap), 4))),
        ];
    }
}

/// <summary>Tag boxes are fractions of the image; whatever the client or the detector sends is clamped into it.</summary>
internal static class TagBoxes
{
    /// <summary>The smallest side of a box (2 % of the image), so a dot always has something to sit on.</summary>
    public const double MinSide = 0.02;

    public static (double X, double Y, double W, double H) Clamp(double x, double y, double w, double h)
    {
        var width = Math.Clamp(double.IsFinite(w) ? w : MinSide, MinSide, 1);
        var height = Math.Clamp(double.IsFinite(h) ? h : MinSide, MinSide, 1);
        var left = Math.Clamp(double.IsFinite(x) ? x : 0, 0, 1 - width);
        var top = Math.Clamp(double.IsFinite(y) ? y : 0, 0, 1 - height);

        return (Math.Round(left, 4), Math.Round(top, 4), Math.Round(width, 4), Math.Round(height, 4));
    }
}
