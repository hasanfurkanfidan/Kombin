using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Notification;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Notification;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Identity;

/// <summary>When the request was made and when the data will be gone (<see cref="AccountDeletionRules.Window"/> later).</summary>
internal sealed record DeletionStatus(DateTimeOffset RequestedAt, DateTimeOffset ScheduledFor);

/// <summary>
/// The first half of account deletion, shared by <c>DELETE /me</c> and the admin panel: <see cref="RequestAsync"/> marks the account pending
/// deletion, ends every session, deactivates every device and hides every post at once; <see cref="CancelAsync"/> and
/// <see cref="RestoreAsync"/> take it back inside the window. Nothing is deleted here: the <c>account-deletion</c> job (<c>AccountPurger</c>) does that
/// once the window is over. The caller proves who is asking (password, ID token, or the founder's session); this class does not.
/// </summary>
internal sealed class AccountDeletion(KombinDbContext db, IDeviceDeactivator devices, IClock clock)
{
    /// <summary>
    /// Posts that are, or could become, visible to others without the author: live ones, ones under review, hidden by reports, and drafts. They
    /// become <see cref="PostStatus.AccountPending"/> (hidden at once, like a deleted post) and remember their status. Posts still being
    /// processed, failed or blocked are invisible already and left alone: a processing one can only end as a draft or a post under review,
    /// and an unconfirmed post can never go live without the author.
    /// </summary>
    private static readonly PostStatus[] Hideable = [PostStatus.Live, PostStatus.NeedsReview, PostStatus.Hidden, PostStatus.Draft];

    public async Task<Result<DeletionStatus>> RequestAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var now = clock.UtcNow;

        // A second request keeps the first date: asking again never pushes the deletion back.
        if (user.DeletionRequestedAt is null)
        {
            user.DeletionRequestedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        // Every step below is safe to repeat, so a request that failed halfway is finished by sending it again.
        await db.Posts
            .Where(post => post.AuthorId == userId && Hideable.Contains(post.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(post => post.StatusBeforeDeletion, post => (PostStatus?)post.Status)
                    .SetProperty(post => post.Status, PostStatus.AccountPending)
                    .SetProperty(post => post.UpdatedAt, now),
                cancellationToken);

        await db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.UpdatedAt, now),
                cancellationToken);

        await devices.DeactivateAllAsync(userId, Device.AccountDeletion, cancellationToken);

        return StatusOf(user);
    }

    /// <summary>The admin panel's "Cancel deletion": takes the request back whatever the window says (the account still exists).</summary>
    public async Task<Result> CancelAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        if (user.DeletionRequestedAt is null)
        {
            return IdentityErrors.DeletionNotPending;
        }

        await RestoreAsync(user, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Clears the request on <paramref name="user"/> (the caller saves) and brings the hidden posts back. The posts come first: if saving the
    /// user then fails the request is still pending and cancelling again repeats the (idempotent) restore.
    /// </summary>
    public async Task RestoreAsync(User user, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await db.Posts
            .Where(post => post.AuthorId == user.Id && post.Status == PostStatus.AccountPending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(post => post.Status, post => post.StatusBeforeDeletion ?? PostStatus.Draft)
                    .SetProperty(post => post.StatusBeforeDeletion, post => null)
                    .SetProperty(post => post.UpdatedAt, now),
                cancellationToken);

        user.DeletionRequestedAt = null;
    }

    /// <summary>The 403 a sign-in gets while the account is pending deletion, with the date so the app can offer to cancel.</summary>
    public static Error PendingError(User user) =>
        IdentityErrors.AccountPendingDeletion.WithExtension("deletionScheduledFor", AccountDeletionRules.ScheduledFor(user.DeletionRequestedAt!.Value));

    public static DeletionStatus StatusOf(User user) =>
        new(user.DeletionRequestedAt!.Value, AccountDeletionRules.ScheduledFor(user.DeletionRequestedAt.Value));
}
