using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Kombin.Api.Features.Identity.Register;

internal sealed record RegisterRequest(string Email, string Password, string DisplayName);

internal static class RegisterEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/register", Handle)
            .WithName("Register")
            .WithValidation<RegisterRequest>()
            .Produces<AuthTokensResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> Handle(
        RegisterRequest request,
        KombinDbContext db,
        IPasswordHasher<User> passwordHasher,
        AuthTokenIssuer tokens,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(user => user.Email == email, cancellationToken))
        {
            return IdentityErrors.EmailTaken.ToHttpResult();
        }

        var user = new User
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            LastLoginAt = clock.UtcNow,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        var issued = tokens.Issue(user, deviceName: null);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race against a concurrent registration of the same email.
            return IdentityErrors.EmailTaken.ToHttpResult();
        }

        return Results.Json(issued.Response, statusCode: StatusCodes.Status201Created);
    }
}
