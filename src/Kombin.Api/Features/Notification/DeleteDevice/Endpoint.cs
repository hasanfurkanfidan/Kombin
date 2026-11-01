using Kombin.Api.Common.Auth;

namespace Kombin.Api.Features.Notification.DeleteDevice;

internal static class DeleteDeviceEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{token}", Handle)
            .WithName("DeleteDevice")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>Idempotent, and silent about tokens that are unknown or belong to someone else.</summary>
    private static async Task<IResult> Handle(string token, ICurrentUser currentUser, IDeviceDeactivator deactivator, CancellationToken cancellationToken)
    {
        await deactivator.DeactivateAsync(currentUser.UserId, token, cancellationToken);

        return TypedResults.NoContent();
    }
}
