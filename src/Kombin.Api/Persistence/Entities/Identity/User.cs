namespace Kombin.Api.Persistence.Entities.Identity;

internal sealed class User : BaseEntity
{
    /// <summary>Always stored trimmed and lower-cased so the unique index is case-insensitive.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Null for an account created through Google or Apple; such a user can only sign in through an <see cref="ExternalLogin"/>.</summary>
    public string? PasswordHash { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// Set when the founder disabled the account (admin panel, <c>AccountAccess</c>): sign-in and refresh are refused and the refresh tokens
    /// are revoked. Nothing else is touched, so enabling it again restores the account as it was. Null for a normal account.
    /// </summary>
    public DateTimeOffset? DisabledAt { get; set; }

    /// <summary>
    /// Set when the user asked to delete the account (<c>DELETE /me</c>, <c>AccountDeletion</c>): the account is then pending deletion. Sign-in is
    /// refused (403 <c>Identity.AccountPendingDeletion</c>) until the user cancels, and the <c>account-deletion</c> job removes the account for good
    /// <c>AccountDeletionRules.Window</c> (7 days) after this instant. Null for a normal account.
    /// </summary>
    public DateTimeOffset? DeletionRequestedAt { get; set; }
}
