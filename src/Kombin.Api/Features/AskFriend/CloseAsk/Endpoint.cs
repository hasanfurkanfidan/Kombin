using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.AskFriend;

namespace Kombin.Api.Features.AskFriend.CloseAsk;

/// <summary><c>POST /api/v1/me/asks/{id}/close</c>: "Linki kapat", taking the link back (design section 3).</summary>
internal static class CloseAskEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/close", Handle)
            .WithName("CloseAsk")
            .RequireRateLimiting(RateLimitPolicies.AskFriend)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Idempotent, also on an expired or hidden ask: an open ask becomes <c>Closed</c> (a hidden one stays hidden), and the picture copies are
    /// deleted in the same request. When the store fails the ask is closed anyway and <c>copies_deleted_at</c> stays empty for the purge.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id, ICurrentUser currentUser, KombinDbContext db, AskImageCopier copier, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var ask = await db.Asks
            .Include(candidate => candidate.Pieces)
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (ask is null)
        {
            return AskFriendErrors.NotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        if (ask.Status == AskStatus.Open)
        {
            ask.Status = AskStatus.Closed;
            ask.ClosedAt = now;
        }

        if (ask.CopiesDeletedAt is null)
        {
            var keys = ask.Pieces.Where(piece => piece.CopyKey is not null).Select(piece => piece.CopyKey!).ToList();
            if (await copier.DeleteAsync(keys, cancellationToken))
            {
                ask.CopiesDeletedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
