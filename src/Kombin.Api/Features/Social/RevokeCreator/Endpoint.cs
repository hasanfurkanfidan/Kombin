using Kombin.Api.Common.Results;
using Kombin.Api.Features.Social.Creators;

namespace Kombin.Api.Features.Social.RevokeCreator;

internal static class RevokeCreatorEndpoint
{
    /// <summary><c>DELETE /internal/social/creators/{userId}</c>: the founder revokes a creator (attribution stops, the page stays reserved).</summary>
    public static void Map(RouteGroupBuilder internalSocial) =>
        internalSocial.MapDelete("/creators/{userId:guid}", Handle)
            .WithName("RevokeCreator")
            .Produces<CreatorProfileResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid userId, CreatorInvites invites, CancellationToken cancellationToken)
    {
        var result = await invites.RevokeAsync(userId, cancellationToken);

        return result.Match<IResult>(profile => TypedResults.Ok(profile), error => error.ToHttpResult());
    }
}
