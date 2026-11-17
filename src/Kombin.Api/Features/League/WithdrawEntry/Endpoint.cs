using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.WithdrawEntry;

internal static class WithdrawEntryEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/me/entry", Handle)
            .WithName("WithdrawLeagueEntry")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// "Bugünkü kombinimi çıkar": today's entry leaves the pool, final for the day (no re-roll: no other worn outfit enters today). Its votes stay
    /// in the day's fit as information about the other outfits; it is not ranked and earns no vote points. Its image copies are deleted by the
    /// next <c>league-entries</c> run. Withdrawing again is a no-op (204). Errors: 403 <c>League.NotJoined</c>; 404 <c>League.NoEntryToday</c> (no
    /// entry, or one removed by a review); 409 <c>League.DayClosed</c> after the day's close.
    /// </summary>
    private static async Task<IResult> Handle(
        ICurrentUser currentUser, KombinDbContext db, IClock clock, IOptions<LeagueOptions> options, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (!await db.LeaguePlayers.AnyAsync(player => player.UserId == userId && player.LeftAt == null, cancellationToken))
        {
            return LeagueErrors.NotJoined.ToHttpResult();
        }

        var now = clock.UtcNow;
        var day = Istanbul.Today(now);
        var closesAt = LeagueDays.ClosesAt(day, options.Value.DayClosesAt);
        if (now >= closesAt)
        {
            return LeagueErrors.DayClosed.ToHttpResult();
        }

        // The day row is held FOR SHARE like a vote holds it, so the close (FOR UPDATE) sees the entry either before or after the withdrawal.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO league.days (day, closes_at, ranked_count, engine_count, pair_count, bt_iterations, bt_converged, close_ms) VALUES ({day}, {closesAt}, 0, 0, 0, 0, false, 0) ON CONFLICT (day) DO NOTHING",
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.days WHERE day = {day} FOR SHARE", cancellationToken);
        if (await db.LeagueDays.AnyAsync(row => row.Day == day && row.ClosedAt != null, cancellationToken))
        {
            return LeagueErrors.DayClosed.ToHttpResult();
        }

        var entry = await db.LeagueEntries.SingleOrDefaultAsync(row => row.Day == day && row.UserId == userId, cancellationToken);
        if (entry is null or { Status: LeagueEntryStatus.Removed })
        {
            return LeagueErrors.NoEntryToday.ToHttpResult();
        }

        if (entry.Status != LeagueEntryStatus.Withdrawn)
        {
            entry.Status = LeagueEntryStatus.Withdrawn;
            entry.Reason = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
