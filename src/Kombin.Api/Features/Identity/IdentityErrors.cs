using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Identity;

internal static class IdentityErrors
{
    public static readonly Error EmailTaken =
        Error.Conflict("Identity.EmailTaken", "An account with this email address already exists.");

    // One message for both "unknown email" and "wrong password" so the response never reveals which accounts exist.
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("Identity.InvalidCredentials", "The email or password is incorrect.");

    public static readonly Error InvalidRefreshToken =
        Error.Unauthorized("Identity.InvalidRefreshToken", "The refresh token is invalid, expired or already used.");

    // One code for every way an ID token can be wrong (expired, bad signature, wrong audience or issuer, nonce mismatch, garbage), so a
    // caller learns nothing about which check failed.
    public static readonly Error ExternalTokenInvalid =
        Error.Unauthorized("Identity.ExternalTokenInvalid", "The sign-in token from the provider is invalid or expired.");

    public static readonly Error ExternalEmailMissing =
        Error.Validation("Identity.ExternalEmailMissing", "The provider did not share a verified email address for this account.");

    public static readonly Error ExternalProviderDisabled =
        Error.Unavailable("Identity.ExternalProviderDisabled", "Sign-in with this provider is not available.");

    public static readonly Error ExternalProviderUnavailable =
        Error.Unavailable("Identity.ExternalProviderUnavailable", "The provider could not be reached to check the sign-in token. Try again.");

    // Said only once the caller proved who they are (right password, valid ID token, live refresh token), so it reveals nothing to a guesser.
    public static readonly Error AccountDisabled =
        Error.Forbidden("Identity.AccountDisabled", "This account has been disabled. Contact support if you think this is a mistake.");

    // Said, like AccountDisabled, only after the caller proved who they are. The 403 carries `deletionScheduledFor` (see AccountDeletion.PendingError).
    public static readonly Error AccountPendingDeletion =
        Error.Forbidden("Identity.AccountPendingDeletion", "This account is scheduled for deletion. Cancel the deletion to keep it.");

    public static readonly Error PasswordRequired =
        Error.Validation("Identity.PasswordRequired", "This account has a password: send it as 'password' to confirm the deletion.");

    // 403 rather than 401: the caller's token is fine, and a 401 would make the app try to refresh or sign out.
    public static readonly Error PasswordIncorrect =
        Error.Forbidden("Identity.PasswordIncorrect", "The password is incorrect.");

    public static readonly Error DeletionNotPending =
        Error.Conflict("Identity.DeletionNotPending", "This account is not scheduled for deletion.");

    public static readonly Error DeletionWindowClosed =
        Error.Conflict("Identity.DeletionWindowClosed", "The cancellation period is over; the account is being deleted.");

    public static readonly Error UserNotFound =
        Error.Unauthorized("Identity.UserNotFound", "The authenticated user no longer exists.");
}
