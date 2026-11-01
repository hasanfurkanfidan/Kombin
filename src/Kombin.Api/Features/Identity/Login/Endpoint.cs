using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.AspNetCore.Identity;

namespace Kombin.Api.Features.Identity.Login;

internal sealed record LoginRequest(string Email, string Password, string? DeviceName = null);

internal static class LoginEndpoint
{
    // Verified when the email is unknown so both failure paths cost the same time.
    private static readonly User DummyUser = new();

    private static readonly string DummyPasswordHash =
        new PasswordHasher<User>().HashPassword(DummyUser, "not-a-real-password-1");

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/login", Handle)
            .WithName("Login")
            .WithValidation<LoginRequest>()
            .Produces<AuthTokensResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(
        LoginRequest request,
        KombinDbContext db,
        IPasswordHasher<User> passwordHasher,
        AuthTokenIssuer tokens,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);

        if (user is null)
        {
            passwordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, request.Password);
            return IdentityErrors.InvalidCredentials.ToHttpResult();
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            return IdentityErrors.InvalidCredentials.ToHttpResult();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        }

        user.LastLoginAt = clock.UtcNow;
        var issued = tokens.Issue(user, request.DeviceName);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(issued.Response);
    }
}
