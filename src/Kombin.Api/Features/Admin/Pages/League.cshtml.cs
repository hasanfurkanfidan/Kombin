using Kombin.Api.Common.Storage;
using Kombin.Api.Features.League;
using Kombin.Api.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Kombin.Api.Features.Admin.Pages;

/// <summary>One reported league outfit as the page shows it: pieces as the voters saw them and the reports, nothing about its owner or its reporters.</summary>
internal sealed record ReviewLeagueEntry(
    Guid Id,
    string Day,
    string Status,
    bool UnderReview,
    int CountingReports,
    int Reports,
    IReadOnlyList<KeyValuePair<string, int>> Reasons,
    IReadOnlyList<ReviewLeaguePiece> Pieces);

internal sealed record ReviewLeaguePiece(string Slot, string? ImageUrl);

/// <summary>
/// The review queue of Kombin Ligi (<c>/admin/league</c>): reported outfits with Restore and Remove, which call <see cref="LeagueEntryReviewer"/>, the
/// one home of the decision (it writes the audit row itself, with the admin's e-mail as the actor). The page never changes a state itself.
/// </summary>
internal sealed class LeagueModel(KombinDbContext db, IObjectStorage storage, LeagueEntryReviewer reviewer) : AdminPageModel
{
    public IReadOnlyList<ReviewLeagueEntry> Entries { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var queue = await LeagueReviewQueue.ListAsync(db, storage, cancellationToken);

        Entries =
        [
            .. queue.Select(item => new ReviewLeagueEntry(
                item.EntryId,
                item.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                item.Status.ToString(),
                item.UnderReview,
                item.CountingReports,
                item.Reports,
                [.. item.Reasons.Select(reason => KeyValuePair.Create(reason.Key, reason.Value))],
                [.. item.Pieces.Select(piece => new ReviewLeaguePiece(piece.Slot.ToString(), piece.ImageUrl))])),
        ];
    }

    public Task<IActionResult> OnPostRestoreAsync(Guid id, CancellationToken cancellationToken) =>
        DecideAsync(id, LeagueReviewDecision.Restore, cancellationToken);

    public Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken cancellationToken) =>
        DecideAsync(id, LeagueReviewDecision.Remove, cancellationToken);

    private async Task<IActionResult> DecideAsync(Guid id, LeagueReviewDecision decision, CancellationToken cancellationToken)
    {
        var result = await reviewer.ReviewAsync(id, decision, Actor, cancellationToken);
        var label = id.ToString()[..8];

        switch (result.Outcome)
        {
            case LeagueReviewOutcome.NotFound:
                SetFlashError($"Outfit {label}: not found (it may be gone already).");
                break;
            case LeagueReviewOutcome.NotReviewable:
                SetFlashError($"Outfit {label}: it was removed, and a removal is final.");
                break;
            default:
                SetFlash($"Outfit {label}: {decision.ToString().ToLowerInvariant()} done (now {result.Status})"
                    + (result.ResultChanged ? "; the closed day's result changed." : "."));
                break;
        }

        return RedirectToPage();
    }
}
