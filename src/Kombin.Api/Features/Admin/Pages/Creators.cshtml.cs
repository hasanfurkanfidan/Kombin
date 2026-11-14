using FluentValidation;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Social;
using Kombin.Api.Features.Social.Creators;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed record CreatorRow(Guid UserId, string Handle, string DisplayName, bool IsActive, int Posts, int LivePosts, int Clicks, decimal EstimatedEarnings);

internal sealed record CreatorCandidate(Guid UserId, string Email, string DisplayName, string? CurrentHandle, bool IsActive, string? Bio, string? Link);

internal sealed class CreatorsModel(
    KombinDbContext db,
    IClock clock,
    IOptions<CreatorOptions> options,
    CreatorInvites invites,
    IValidator<PutCreatorRequest> validator) : AdminPageModel
{
    public const int Days = 30;

    private const int MaxMatches = 10;

    public IReadOnlyList<CreatorRow> Creators { get; private set; } = [];

    public IReadOnlyList<CreatorCandidate> Matches { get; private set; } = [];

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadCreatorsAsync(cancellationToken);
        await LoadMatchesAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostInviteAsync(Guid userId, string? handle, string? bio, string? link, CancellationToken cancellationToken)
    {
        var request = new PutCreatorRequest(handle, bio, link, true);
        var validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            SetFlashError(string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
            return RedirectToPage();
        }

        var result = await invites.PutAsync(userId, request, cancellationToken);

        if (result.IsFailure)
        {
            SetFlashError(result.Error.Description);
            return RedirectToPage();
        }

        await AuditAsync("creator.invite", "creator", userId.ToString(), $"handle={result.Value.Handle}", cancellationToken);
        SetFlash($"@{result.Value.Handle} is a creator.");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var result = await invites.RevokeAsync(userId, cancellationToken);

        if (result.IsFailure)
        {
            SetFlashError(result.Error.Description);
            return RedirectToPage();
        }

        await AuditAsync("creator.revoke", "creator", userId.ToString(), $"handle={result.Value.Handle}", cancellationToken);
        SetFlash($"@{result.Value.Handle} is revoked.");

        return RedirectToPage();
    }

    private async Task LoadCreatorsAsync(CancellationToken cancellationToken)
    {
        var since = clock.UtcNow.AddDays(-Days);
        var creators = await db.CreatorProfiles.AsNoTracking()
            .Join(db.Users, creator => creator.UserId, user => user.Id, (creator, user) => new { creator.UserId, creator.Handle, creator.IsActive, user.DisplayName })
            .OrderBy(row => row.Handle)
            .ToListAsync(cancellationToken);
        var ids = creators.Select(creator => creator.UserId).ToList();

        var posts = (await db.Posts.AsNoTracking()
                .Where(post => ids.Contains(post.AuthorId) && post.Status != PostStatus.Deleted)
                .GroupBy(post => post.AuthorId)
                .Select(group => new { AuthorId = group.Key, All = group.Count(), Live = group.Count(post => post.Status == PostStatus.Live) })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.AuthorId);
        var clicks = (await db.Clicks.AsNoTracking()
                .Where(click => click.CreatorId != null && ids.Contains(click.CreatorId.Value) && click.ClickedAt >= since)
                .GroupBy(click => click.CreatorId!.Value)
                .Select(group => new { CreatorId = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.CreatorId, row => row.Count);

        Creators =
        [
            .. creators.Select(creator =>
            {
                var clickCount = clicks.GetValueOrDefault(creator.UserId);
                var post = posts.GetValueOrDefault(creator.UserId);

                return new CreatorRow(
                    creator.UserId,
                    creator.Handle,
                    creator.DisplayName,
                    creator.IsActive,
                    post?.All ?? 0,
                    post?.Live ?? 0,
                    clickCount,
                    CreatorEarnings.Estimate(clickCount, options.Value).Amount);
            }),
        ];
    }

    private async Task LoadMatchesAsync(CancellationToken cancellationToken)
    {
        var query = Query?.Trim().TrimStart('@').ToLowerInvariant();

        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var asId = Guid.TryParse(query, out var id) ? id : (Guid?)null;
        var users = await db.Users.AsNoTracking()
            .Where(user => (asId != null && user.Id == asId)
                || user.Email.Contains(query)
                || db.CreatorProfiles.Any(creator => creator.UserId == user.Id && creator.Handle == query))
            .OrderBy(user => user.Email)
            .Take(MaxMatches)
            .Select(user => new { user.Id, user.Email, user.DisplayName })
            .ToListAsync(cancellationToken);
        var ids = users.Select(user => user.Id).ToList();
        var profiles = await db.CreatorProfiles.AsNoTracking().Where(creator => ids.Contains(creator.UserId)).ToDictionaryAsync(creator => creator.UserId, cancellationToken);

        Matches =
        [
            .. users.Select(user =>
            {
                var profile = profiles.GetValueOrDefault(user.Id);

                return new CreatorCandidate(user.Id, user.Email, user.DisplayName, profile?.Handle, profile?.IsActive ?? false, profile?.Bio, profile?.Link);
            }),
        ];
    }
}
