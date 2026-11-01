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

    public static readonly Error UserNotFound =
        Error.Unauthorized("Identity.UserNotFound", "The authenticated user no longer exists.");
}
