using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Identity.GetMe;

internal sealed record MeResponse(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt);

internal static class GetMeEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("GetMe")
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var me = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new MeResponse(user.Id, user.Email, user.DisplayName, user.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);

        return me is null ? IdentityErrors.UserNotFound.ToHttpResult() : Results.Ok(me);
    }
}
