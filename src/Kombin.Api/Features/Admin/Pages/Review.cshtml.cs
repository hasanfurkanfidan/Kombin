using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Social;
using Kombin.Api.Features.Social.Review;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.AspNetCore.Mvc;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed record ReviewPost(
    Guid Id,
    Guid AuthorId,
    string AuthorName,
    string Kind,
    string Status,
    string Visibility,
    string? Caption,
    int ReportCount,
    IReadOnlyDictionary<string, int> ReportReasons,
    IReadOnlyList<ModerationLine> Moderation,
    string? ModerationJson,
    string? FeedImageUrl,
    DateTimeOffset UpdatedAt);

internal sealed class ReviewModel(KombinDbContext db, IObjectStorage storage, PostReviewer reviewer) : AdminPageModel
{
    public IReadOnlyList<ReviewPost> Posts { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var posts = await db.Posts.AsNoTracking()
            .Where(post => post.Status == PostStatus.Hidden || post.Status == PostStatus.NeedsReview)
            .OrderBy(post => post.UpdatedAt)
            .ThenBy(post => post.Id)
            .Take(ReviewEndpoint.QueueLimit)
            .ToListAsync(cancellationToken);
        var ids = posts.Select(post => post.Id).ToList();
        var authorIds = posts.Select(post => post.AuthorId).Distinct().ToList();

        var reasons = (await db.PostReports.AsNoTracking()
                .Where(report => ids.Contains(report.PostId))
                .GroupBy(report => new { report.PostId, report.Reason })
                .Select(group => new { group.Key.PostId, group.Key.Reason, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.PostId);
        var authors = await db.Users.AsNoTracking()
            .Where(user => authorIds.Contains(user.Id))
            .Select(user => new { user.Id, user.DisplayName })
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);

        Posts =
        [
            .. posts.Select(post => new ReviewPost(
                post.Id,
                post.AuthorId,
                authors.GetValueOrDefault(post.AuthorId, "(deleted user)"),
                post.Kind.ToString(),
                post.Status.ToString(),
                post.Visibility.ToString(),
                post.Caption,
                post.ReportCount,
                reasons[post.Id].ToDictionary(row => row.Reason.ToString(), row => row.Count),
                ModerationView.Parse(post.ModerationJson),
                post.ModerationJson,
                post.ImageFeedKey is { } key ? storage.GetPresignedDownloadUrl(key, SocialLimits.ImageUrlLifetime) : null,
                post.UpdatedAt)),
        ];
    }

    public Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken) =>
        DecideAsync(id, ReviewDecision.Approve, null, "post.approve", cancellationToken);

    public Task<IActionResult> OnPostKeepAsync(Guid id, CancellationToken cancellationToken) =>
        DecideAsync(id, ReviewDecision.Keep, null, "post.keep", cancellationToken);

    public async Task<IActionResult> OnPostBlockAsync(Guid id, string? reasonKey, string? custom, CancellationToken cancellationToken)
    {
        var reason = BlockReasons.Compose(reasonKey, custom);

        if (reason.Length > Post.MaxModerationReasonLength)
        {
            SetFlashError($"The reason is too long ({reason.Length} of {Post.MaxModerationReasonLength} characters): shorten your own words.");
            return RedirectToPage();
        }

        return await DecideAsync(id, ReviewDecision.Remove, reason, "post.block", cancellationToken);
    }

    private async Task<IActionResult> DecideAsync(Guid id, ReviewDecision decision, string? reason, string action, CancellationToken cancellationToken)
    {
        var result = await reviewer.ReviewAsync(id, decision, reason, cancellationToken);

        if (result.IsFailure)
        {
            SetFlashError(result.Error.Description);
            return RedirectToPage();
        }

        await AuditAsync(action, "post", id.ToString(), reason is null ? $"status={result.Value.Status}" : $"status={result.Value.Status}; reason={reason}", cancellationToken);
        SetFlash($"Post {id.ToString()[..8]}: {action["post.".Length..]} done (now {result.Value.Status}).");

        return RedirectToPage();
    }
}
