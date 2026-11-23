using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.AskFriend.GetAsk;

/// <summary><c>GET /api/v1/me/asks/{id}</c>: one ask of the caller with its tally and answers; someone else's is 404.</summary>
internal static class GetAskEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}", Handle)
            .WithName("GetAsk")
            .Produces<AskResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(
        Guid id,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        IOptions<AskFriendOptions> options,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var ask = await db.Asks.AsNoTracking()
            .Include(candidate => candidate.Pieces)
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (ask is null)
        {
            return AskFriendErrors.NotFound.ToHttpResult();
        }

        return TypedResults.Ok(await AskResponses.BuildAsync(db, storage, options.Value, ask, clock.UtcNow, cancellationToken));
    }
}
