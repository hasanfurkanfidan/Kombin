using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.AskFriend.GetConsent;

/// <summary>The current ask consent and whether the caller already accepted this version (with an earlier ask).</summary>
internal sealed record AskConsentResponse(string Version, string Text, bool Accepted);

/// <summary><c>GET /api/v1/me/asks/consent</c>: the sheet shown before the first ask under a version.</summary>
internal static class GetAskConsentEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/consent", Handle)
            .WithName("GetAskConsent")
            .Produces<AskConsentResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var current = AskFriendConsent.Current;
        var userId = currentUser.UserId;
        var accepted = await db.AskConsents.AsNoTracking().AnyAsync(consent => consent.UserId == userId && consent.Version == current.Version, cancellationToken);

        return TypedResults.Ok(new AskConsentResponse(current.Version, current.Text, accepted));
    }
}
