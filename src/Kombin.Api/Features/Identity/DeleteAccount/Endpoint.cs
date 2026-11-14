using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Kombin.Api.Features.Identity.DeleteAccount;

/// <summary><c>Password</c> is required for an account that has one; <c>Confirm</c> is the word "SİL" (any casing).</summary>
internal sealed record DeleteAccountRequest(string? Password, string? Confirm);

/// <summary><c>DeletionScheduledFor</c> is the date the data will be gone; signing in again before it offers to cancel.</summary>
internal sealed record DeleteAccountResponse(DateTimeOffset DeletionRequestedAt, DateTimeOffset DeletionScheduledFor);

internal static class DeleteAccountEndpoint
{
    /// <summary>
    /// <c>DELETE /me</c>: asks to delete the account. It is pending at once (every session and device ended, every post hidden, sign-in answers
    /// 403 <c>Identity.AccountPendingDeletion</c>) and gone for good after 7 days (<c>account-deletion</c>); <c>POST /auth/cancel-deletion</c> takes
    /// it back before. A password account must send its password, a Google/Apple-only account has none to send. The body is read explicitly
    /// because minimal APIs do not infer one for DELETE. Behind the <c>auth</c> rate limit: a stolen access token must not become a password oracle.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/", Handle)
            .WithName("DeleteAccount")
            .WithValidation<DeleteAccountRequest>()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces<DeleteAccountResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    private static async Task<IResult> Handle(
        [FromBody] DeleteAccountRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IPasswordHasher<User> passwordHasher,
        AccountDeletion deletion,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null)
        {
            return IdentityErrors.UserNotFound.ToHttpResult();
        }

        if (user.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(request.Password))
            {
                return IdentityErrors.PasswordRequired.ToHttpResult();
            }

            if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            {
                return IdentityErrors.PasswordIncorrect.ToHttpResult();
            }
        }

        var result = await deletion.RequestAsync(userId, cancellationToken);

        return result.Match<IResult>(
            status => TypedResults.Accepted((string?)null, new DeleteAccountResponse(status.RequestedAt, status.ScheduledFor)),
            error => error.ToHttpResult());
    }
}
