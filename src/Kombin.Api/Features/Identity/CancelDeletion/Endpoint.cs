using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Identity.ExternalTokens;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.AspNetCore.Identity;

namespace Kombin.Api.Features.Identity.CancelDeletion;

/// <summary>
/// The same credentials as signing in: <c>Email</c> and <c>Password</c>, or <c>Provider</c> and <c>IdToken</c> (with <c>Nonce</c> when the app
/// used one). They are what proves who is cancelling; nothing else in the body is trusted.
/// </summary>
internal sealed record CancelDeletionRequest(
    string? Email = null,
    string? Password = null,
    string? Provider = null,
    string? IdToken = null,
    string? Nonce = null,
    string? DeviceName = null,
    string? AnonymousId = null);

internal static class CancelDeletionEndpoint
{
    // Verified when the email is unknown so both failure paths cost the same time (the same trick as the login).
    private static readonly User DummyUser = new();

    private static readonly string DummyPasswordHash =
        new PasswordHasher<User>().HashPassword(DummyUser, "not-a-real-password-1");

    /// <summary>
    /// <c>POST /auth/cancel-deletion</c>: the user changed their mind within the 7 days. Credentials as for sign-in; success restores the account
    /// (the hidden posts come back; devices must register again) and signs the user in, answering like <c>/auth/login</c>. A wrong password or
    /// ID token is the usual 401, <c>Identity.DeletionNotPending</c> / <c>Identity.DeletionWindowClosed</c> (409) are said only after the
    /// credentials were right.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/cancel-deletion", Handle)
            .WithName("CancelDeletion")
            .WithValidation<CancelDeletionRequest>()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces<AuthTokensResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> Handle(
        CancelDeletionRequest request,
        KombinDbContext db,
        IPasswordHasher<User> passwordHasher,
        IExternalTokenVerifier verifier,
        AccountDeletion deletion,
        AuthTokenIssuer tokens,
        IAnalyticsIdentityLinker analyticsLinker,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var proven = string.IsNullOrEmpty(request.IdToken)
            ? await ProveWithPasswordAsync(request, db, passwordHasher, cancellationToken)
            : await ProveWithTokenAsync(request, db, verifier, cancellationToken);

        if (proven.IsFailure)
        {
            return proven.Error.ToHttpResult();
        }

        var user = proven.Value;

        if (user.DisabledAt is not null)
        {
            return IdentityErrors.AccountDisabled.ToHttpResult();
        }

        var now = clock.UtcNow;

        if (user.DeletionRequestedAt is null)
        {
            return IdentityErrors.DeletionNotPending.ToHttpResult();
        }

        if (!AccountDeletionRules.CanCancel(user.DeletionRequestedAt, now))
        {
            return IdentityErrors.DeletionWindowClosed.ToHttpResult();
        }

        await deletion.RestoreAsync(user, cancellationToken);
        user.LastLoginAt = now;
        var issued = tokens.Issue(user, request.DeviceName);
        await db.SaveChangesAsync(cancellationToken);
        await analyticsLinker.LinkAsync(user.Id, request.AnonymousId, cancellationToken);

        return Results.Ok(issued.Response);
    }

    private static async Task<Result<User>> ProveWithPasswordAsync(
        CancelDeletionRequest request, KombinDbContext db, IPasswordHasher<User> passwordHasher, CancellationToken cancellationToken)
    {
        var email = request.Email!.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);

        // A passwordless account fails like an unknown email, as at login.
        if (user?.PasswordHash is null)
        {
            passwordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, request.Password!);
            return IdentityErrors.InvalidCredentials;
        }

        return passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password!) == PasswordVerificationResult.Failed
            ? IdentityErrors.InvalidCredentials
            : user;
    }

    private static async Task<Result<User>> ProveWithTokenAsync(
        CancelDeletionRequest request, KombinDbContext db, IExternalTokenVerifier verifier, CancellationToken cancellationToken)
    {
        var provider = request.Provider!.Trim().ToLowerInvariant();
        var nonce = string.IsNullOrWhiteSpace(request.Nonce) ? null : request.Nonce;
        var verified = await verifier.VerifyAsync(provider, request.IdToken!.Trim(), nonce, cancellationToken);

        if (verified.IsFailure)
        {
            return verified.Error;
        }

        var identity = verified.Value;

        var linked = await db.ExternalLogins
            .Where(login => login.Provider == identity.Provider && login.Subject == identity.Subject)
            .Join(db.Users, login => login.UserId, user => user.Id, (_, user) => user)
            .SingleOrDefaultAsync(cancellationToken);

        if (linked is not null)
        {
            return linked;
        }

        // Not linked yet (the account was made with a password): the provider's verified email is the same proof it is for sign-in.
        if (identity.Email is not null && identity.EmailVerified)
        {
            var byEmail = await db.Users.SingleOrDefaultAsync(user => user.Email == identity.Email, cancellationToken);

            if (byEmail is not null)
            {
                return byEmail;
            }
        }

        return IdentityErrors.InvalidCredentials;
    }
}
