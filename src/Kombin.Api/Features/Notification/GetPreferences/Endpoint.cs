using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Notification.GetPreferences;

internal static class GetPreferencesEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("GetNotificationPreferences")
            .Produces<PreferencesResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var preference = await NotificationPreferences.GetOrCreateAsync(db, currentUser.UserId, cancellationToken);

        return TypedResults.Ok(PreferencesResponse.From(preference));
    }
}
