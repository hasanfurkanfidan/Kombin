using System.Linq.Expressions;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Social;

/// <summary>
/// <c>Reports:*</c>: who may hide a post by reporting it (security review M4). Every report reaches the review queue; only a trusted reporter's
/// report counts toward the automatic hide, so three throwaway accounts cannot silence anyone.
/// </summary>
internal sealed class ReportOptions
{
    public const string SectionName = "Reports";

    /// <summary>Counting reports since the last review that hide a live post.</summary>
    public int HideAt { get; set; } = PostRules.HideAtReports;

    /// <summary>A reporter's account must be at least this old when they report.</summary>
    public TimeSpan MinAccountAge { get; set; } = TimeSpan.FromHours(72);

    /// <summary>And must have confirmed onboarding: at least this many personas that are not archived.</summary>
    public int MinPersonas { get; set; } = 1;

    /// <summary>A reporter counts once per author in this window, whichever of the author's posts they report.</summary>
    public TimeSpan PerAuthorWindow { get; set; } = TimeSpan.FromHours(24);
}

internal sealed class ReportOptionsValidator : IValidateOptions<ReportOptions>
{
    public ValidateOptionsResult Validate(string? name, ReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.HideAt >= 1 && options.MinAccountAge >= TimeSpan.Zero && options.MinPersonas >= 0 && options.PerAuthorWindow >= TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Reports: HideAt must be at least 1; MinAccountAge, MinPersonas and PerAuthorWindow must not be negative.");
    }
}

/// <summary>The rules of who counts toward the automatic hide, pure (unit-tested in <c>SocialRulesTests</c>).</summary>
internal static class ReportRules
{
    /// <summary>
    /// A report counts when the reporter's account is at least <see cref="ReportOptions.MinAccountAge"/> old, has at least
    /// <see cref="ReportOptions.MinPersonas"/> personas, and has no counting report on any post of the same author within
    /// <see cref="ReportOptions.PerAuthorWindow"/>.
    /// </summary>
    public static bool Counts(DateTimeOffset accountCreatedAt, int personas, bool countedForAuthorRecently, DateTimeOffset now, ReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return now - accountCreatedAt >= options.MinAccountAge && personas >= options.MinPersonas && !countedForAuthorRecently;
    }

    /// <summary>
    /// What the review queue holds (the internal endpoint, the admin page and the dashboard count share it): hidden and borderline posts, and
    /// live posts with a report since their last review, counting or not, so every report reaches a person.
    /// </summary>
    public static readonly Expression<Func<Post, bool>> InReviewQueue = post =>
        post.Status == PostStatus.Hidden || post.Status == PostStatus.NeedsReview || (post.Status == PostStatus.Live && post.ReportCount > 0);
}
