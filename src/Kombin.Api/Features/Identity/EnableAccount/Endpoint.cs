using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Identity.EnableAccount;

internal static class EnableAccountEndpoint
{
    /// <summary><c>POST /internal/identity/users/{userId}/enable</c>: undoes a disable; the user signs in again with their password or provider.</summary>
    public static void Map(RouteGroupBuilder internalIdentity) =>
        internalIdentity.MapPost("/users/{userId:guid}/enable", Handle)
            .WithName("EnableAccount")
            .Produces<AccountAccessResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid userId, AccountAccess access, CancellationToken cancellationToken)
    {
        var result = await access.EnableAsync(userId, cancellationToken);

        return result.Match<IResult>(response => TypedResults.Ok(response), error => error.ToHttpResult());
    }
}
