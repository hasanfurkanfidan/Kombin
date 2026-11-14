using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Identity.DisableAccount;

internal static class DisableAccountEndpoint
{
    /// <summary><c>POST /internal/identity/users/{userId}/disable</c>: refuses the account's sign-in and refresh and revokes its refresh tokens.</summary>
    public static void Map(RouteGroupBuilder internalIdentity) =>
        internalIdentity.MapPost("/users/{userId:guid}/disable", Handle)
            .WithName("DisableAccount")
            .Produces<AccountAccessResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid userId, AccountAccess access, CancellationToken cancellationToken)
    {
        var result = await access.DisableAsync(userId, cancellationToken);

        return result.Match<IResult>(response => TypedResults.Ok(response), error => error.ToHttpResult());
    }
}
