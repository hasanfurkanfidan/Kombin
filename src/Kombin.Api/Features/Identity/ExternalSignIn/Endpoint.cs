using System.Globalization;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Identity.ExternalTokens;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Npgsql;

namespace Kombin.Api.Features.Identity.ExternalSignIn;

/// <summary>
/// An ID token the app obtained from Google or Apple on the device. <c>DisplayName</c> is used only when this sign-in creates the account
/// (Apple gives the app the user's name once, on the very first sign-in). <c>Nonce</c> is the raw nonce the app generated for the provider,
/// when it used one. Nothing in the body is trusted for identity: who the user is comes from the verified token's claims alone.
/// </summary>
internal sealed record ExternalSignInRequest(
    string Provider,
    string IdToken,
    string? DisplayName = null,
    string? DeviceName = null,
    string? AnonymousId = null,
    string? Nonce = null);

internal static class ExternalSignInEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/external", Handle)
            .WithName("ExternalSignIn")
            .WithValidation<ExternalSignInRequest>()
            .Produces<AuthTokensResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> Handle(
        ExternalSignInRequest request,
        KombinDbContext db,
        IExternalTokenVerifier verifier,
        AuthTokenIssuer tokens,
        IAnalyticsIdentityLinker analyticsLinker,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var provider = request.Provider.Trim().ToLowerInvariant();
        var nonce = string.IsNullOrWhiteSpace(request.Nonce) ? null : request.Nonce;
        var verified = await verifier.VerifyAsync(provider, request.IdToken.Trim(), nonce, cancellationToken);

        if (verified.IsFailure)
        {
            return verified.Error.ToHttpResult();
        }

        Result<SignedIn> signedIn;

        try
        {
            signedIn = await SignInAsync(db, tokens, clock, verified.Value, request, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race against a simultaneous first sign-in with the same token (unique provider + subject) or the same email (unique
            // users.email). The winner's rows are committed now, so the lookup finds them the second time.
            db.ChangeTracker.Clear();
            signedIn = await SignInAsync(db, tokens, clock, verified.Value, request, cancellationToken);
        }

        if (signedIn.IsFailure)
        {
            return signedIn.Error.ToHttpResult();
        }

        // What register and login do after their commit, by the same call.
        await analyticsLinker.LinkAsync(signedIn.Value.UserId, request.AnonymousId, cancellationToken);

        return Results.Ok(signedIn.Value.Response);
    }

    private static async Task<Result<SignedIn>> SignInAsync(
        KombinDbContext db,
        AuthTokenIssuer tokens,
        IClock clock,
        ExternalIdentity identity,
        ExternalSignInRequest request,
        CancellationToken cancellationToken)
    {
        var linkedUser = await db.ExternalLogins
            .Where(login => login.Provider == identity.Provider && login.Subject == identity.Subject)
            .Join(db.Users, login => login.UserId, user => user.Id, (_, user) => user)
            .SingleOrDefaultAsync(cancellationToken);

        var userWithEmail = linkedUser is null && identity.Email is not null
            ? await db.Users.SingleOrDefaultAsync(user => user.Email == identity.Email, cancellationToken)
            : null;

        var step = ExternalSignInRules.Decide(identity, linkedUser is not null, userWithEmail is not null);

        if (step.IsFailure)
        {
            return step.Error;
        }

        User user;

        switch (step.Value)
        {
            case ExternalSignInStep.SignInLinked:
                user = linkedUser!;
                break;

            case ExternalSignInStep.LinkByEmail:
                user = userWithEmail!;
                db.ExternalLogins.Add(NewLink(user, identity));
                break;

            default:
                user = new User
                {
                    Email = identity.Email!,
                    PasswordHash = null,
                    DisplayName = ExternalSignInRules.ChooseDisplayName(request.DisplayName, identity.Name, identity.Email!),
                };
                db.Users.Add(user);
                db.ExternalLogins.Add(NewLink(user, identity));
                break;
        }

        var isNewUser = step.Value == ExternalSignInStep.CreateUser;
        user.LastLoginAt = clock.UtcNow;
        var issued = tokens.Issue(user, request.DeviceName, isNewUser);
        await db.SaveChangesAsync(cancellationToken);

        return new SignedIn(user.Id, issued.Response);
    }

    private static ExternalLogin NewLink(User user, ExternalIdentity identity) => new()
    {
        UserId = user.Id,
        Provider = identity.Provider,
        Subject = identity.Subject,
        EmailAtLink = identity.Email,
    };

    private sealed record SignedIn(Guid UserId, AuthTokensResponse Response);
}

internal enum ExternalSignInStep
{
    /// <summary>The provider account is already linked: sign that user in.</summary>
    SignInLinked,

    /// <summary>Not linked, but an account with the token's verified email exists: link the provider account to it.</summary>
    LinkByEmail,

    /// <summary>Neither: create a passwordless account and link it.</summary>
    CreateUser,
}

/// <summary>The find-or-create rules of <c>POST /auth/external</c>, pure so they are unit-tested without a database.</summary>
internal static class ExternalSignInRules
{
    public const int DisplayNameMaxLength = 50;

    /// <summary>
    /// 1. A link for (provider, subject) wins, whatever the email says now. 2. Otherwise the token must carry a verified email, because
    /// linking and creating are keyed on it (Google: <c>email_verified</c>; Apple verifies every email it shares). 3. An account with that
    /// email gets the link; linking by email is safe only because the provider verified it. 4. Otherwise a new account.
    /// </summary>
    public static Result<ExternalSignInStep> Decide(ExternalIdentity identity, bool linkExists, bool userWithEmailExists)
    {
        if (linkExists)
        {
            return ExternalSignInStep.SignInLinked;
        }

        if (string.IsNullOrWhiteSpace(identity.Email) || !identity.EmailVerified)
        {
            return IdentityErrors.ExternalEmailMissing;
        }

        return userWithEmailExists ? ExternalSignInStep.LinkByEmail : ExternalSignInStep.CreateUser;
    }

    /// <summary>The name the app sent, else the token's <c>name</c>, else the local part of the email; trimmed and cut to 50 characters.</summary>
    public static string ChooseDisplayName(string? requested, string? tokenName, string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var localPart = at > 0 ? email[..at] : email;

        var name = new[] { requested, tokenName, localPart }
            .Select(candidate => candidate?.Trim())
            .First(candidate => !string.IsNullOrEmpty(candidate))!;

        return Truncate(name, DisplayNameMaxLength);
    }

    // Cuts on text-element boundaries so an emoji or a combining mark is never split in half.
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);

        while (elements.MoveNext())
        {
            var next = length + elements.GetTextElement().Length;

            if (next > maxLength)
            {
                break;
            }

            length = next;
        }

        return value[..length].TrimEnd();
    }
}
