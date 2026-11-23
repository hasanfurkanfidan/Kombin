using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.AskFriend.HideAnswer;

/// <summary><c>DELETE /api/v1/me/asks/{id}/answers/{answerId}</c>: "Gizle", the asker hides one answer (it leaves the tally and the list).</summary>
internal static class HideAnswerEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}/answers/{answerId:guid}", Handle)
            .WithName("HideAskAnswer")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Idempotent: the first call sets <c>hidden_at</c>. Someone else's ask, or an answer of another ask, is 404. A bulk update, so the answer's
    /// <c>updated_at</c> (its <c>answeredAt</c>) is left as it was.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id, Guid answerId, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var found = await db.AskAnswers.AsNoTracking()
            .AnyAsync(answer => answer.Id == answerId && answer.AskId == id && db.Asks.Any(ask => ask.Id == id && ask.UserId == userId), cancellationToken);
        if (!found)
        {
            return AskFriendErrors.NotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        await db.AskAnswers
            .Where(answer => answer.Id == answerId && answer.HiddenAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(answer => answer.HiddenAt, now), cancellationToken);

        return TypedResults.NoContent();
    }
}
