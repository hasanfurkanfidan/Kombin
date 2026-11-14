using Kombin.Api.Common.Time;
using Kombin.Api.Features.Identity;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.AspNetCore.Mvc;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed record UserMatch(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? DisabledAt);

internal sealed record UserDetail(
    Guid Id,
    string Email,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? DisabledAt,
    bool HasPassword,
    IReadOnlyList<string> Providers,
    string? Audience,
    bool OnboardingDone,
    int Personas,
    int ArchivedPersonas,
    int WardrobeItems,
    int Posts,
    int LivePosts,
    int BlocksMade,
    int BlockedBy,
    string? CreatorHandle,
    int ActiveTokens);

internal sealed class UsersModel(KombinDbContext db, AccountAccess access, IClock clock) : AdminPageModel
{
    private const int MaxMatches = 20;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "id")]
    public Guid? UserId { get; set; }

    public IReadOnlyList<UserMatch> Matches { get; private set; } = [];

    public UserDetail? Detail { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var query = Query?.Trim().ToLowerInvariant();

        if (UserId is null && !string.IsNullOrEmpty(query))
        {
            var asId = Guid.TryParse(query, out var id) ? id : (Guid?)null;
            Matches = await db.Users.AsNoTracking()
                .Where(user => (asId != null && user.Id == asId) || user.Email.Contains(query))
                .OrderBy(user => user.Email)
                .Take(MaxMatches)
                .Select(user => new UserMatch(user.Id, user.Email, user.DisplayName, user.CreatedAt, user.LastLoginAt, user.DisabledAt))
                .ToListAsync(cancellationToken);

            if (Matches.Count == 1)
            {
                UserId = Matches[0].Id;
                Matches = [];
            }
        }

        if (UserId is { } userId)
        {
            Detail = await LoadDetailAsync(userId, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostDisableAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await access.DisableAsync(id, cancellationToken);

        if (result.IsFailure)
        {
            SetFlashError(result.Error.Description);
            return RedirectToPage(new { id });
        }

        await AuditAsync("user.disable", "user", id.ToString(), $"revokedTokens={result.Value.RevokedTokens}", cancellationToken);
        SetFlash($"Account disabled; {result.Value.RevokedTokens} session(s) ended. Access tokens already issued work until they expire (at most the configured access token lifetime).");

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostEnableAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await access.EnableAsync(id, cancellationToken);

        if (result.IsFailure)
        {
            SetFlashError(result.Error.Description);
            return RedirectToPage(new { id });
        }

        await AuditAsync("user.enable", "user", id.ToString(), null, cancellationToken);
        SetFlash("Account re-enabled.");

        return RedirectToPage(new { id });
    }

    private async Task<UserDetail?> LoadDetailAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null)
        {
            return null;
        }

        var profile = await db.UserProfiles.AsNoTracking().Where(candidate => candidate.UserId == userId).Select(candidate => new { Audience = candidate.Audience.ToString(), Done = candidate.OnboardingCompletedAt != null }).SingleOrDefaultAsync(cancellationToken);
        var providers = await db.ExternalLogins.AsNoTracking().Where(login => login.UserId == userId).Select(login => login.Provider).ToListAsync(cancellationToken);
        var personas = await db.Personas.AsNoTracking().Where(persona => persona.UserId == userId).GroupBy(persona => persona.IsArchived).Select(group => new { Archived = group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        var wardrobe = await db.WardrobeItems.AsNoTracking().VisibleOf(userId).CountAsync(cancellationToken);
        var posts = await db.Posts.AsNoTracking().Where(post => post.AuthorId == userId && post.Status != PostStatus.Deleted).GroupBy(post => post.Status == PostStatus.Live).Select(group => new { Live = group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        var blocksMade = await db.UserBlocks.CountAsync(block => block.BlockerId == userId, cancellationToken);
        var blockedBy = await db.UserBlocks.CountAsync(block => block.BlockedId == userId, cancellationToken);
        var creator = await db.CreatorProfiles.AsNoTracking().Where(candidate => candidate.UserId == userId).Select(candidate => candidate.Handle).SingleOrDefaultAsync(cancellationToken);
        var now = clock.UtcNow;
        var tokens = await db.RefreshTokens.CountAsync(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > now, cancellationToken);

        return new UserDetail(
            user.Id,
            user.Email,
            user.DisplayName,
            user.CreatedAt,
            user.LastLoginAt,
            user.DisabledAt,
            user.PasswordHash is not null,
            providers,
            profile?.Audience,
            profile?.Done ?? false,
            personas.Where(row => !row.Archived).Sum(row => row.Count),
            personas.Where(row => row.Archived).Sum(row => row.Count),
            wardrobe,
            posts.Sum(row => row.Count),
            posts.Where(row => row.Live).Sum(row => row.Count),
            blocksMade,
            blockedBy,
            creator,
            tokens);
    }
}
