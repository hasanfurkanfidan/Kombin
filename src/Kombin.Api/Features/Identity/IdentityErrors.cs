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

    public static readonly Error UserNotFound =
        Error.Unauthorized("Identity.UserNotFound", "The authenticated user no longer exists.");
}
