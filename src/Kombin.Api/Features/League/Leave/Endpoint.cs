using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.Leave;

internal static class LeaveLeagueEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/leave", Handle)
            .WithName("LeaveLeague")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// "Ligden ayrıl" (settings): no outfit enters from now on and today's entry is withdrawn. The history stays for the player (and goes with
    /// the account); joining again keeps the tier. Not a player (never joined, or already left): 403 <c>League.NotJoined</c>. Takes the player's
    /// day lock (<see cref="EntryMaterializer.LockPlayerDayAsync"/>), so an entry being resolved at the same moment is either withdrawn here or
    /// never written. After the day's close time (21:00) today's entry is left as it is: the day is over and its close scores it (the player
    /// left after it, so they still get that day's row); before it, the day row is held <c>FOR SHARE</c> like a vote holds it, so the close sees
    /// the entry either before or after the withdrawal.
    /// </summary>
    private static async Task<IResult> Handle(
        ICurrentUser currentUser, KombinDbContext db, IClock clock, IOptions<LeagueOptions> options, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var player = await db.LeaguePlayers.SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        if (player is not { IsJoined: true })
        {
            return LeagueErrors.NotJoined.ToHttpResult();
        }

        var now = clock.UtcNow;
        var today = Istanbul.Today(now);

        var closesAt = LeagueDays.ClosesAt(today, options.Value.DayClosesAt);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EntryMaterializer.LockPlayerDayAsync(db, userId, today, cancellationToken);
        var dayOpen = false;
        if (now < closesAt)
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO league.days (day, closes_at, ranked_count, engine_count, pair_count, bt_iterations, bt_converged, close_ms) VALUES ({today}, {closesAt}, 0, 0, 0, 0, false, 0) ON CONFLICT (day) DO NOTHING",
                cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.days WHERE day = {today} FOR SHARE", cancellationToken);
            dayOpen = !await db.LeagueDays.AnyAsync(row => row.Day == today && row.ClosedAt != null, cancellationToken);
        }

        player.LeftAt = now;
        db.LeagueConsents.Add(new LeagueConsentRecord { UserId = userId, Action = LeagueConsentAction.Leave, At = now });
        await db.SaveChangesAsync(cancellationToken);

        // A removed entry stays removed; anything else of today leaves the pool, while the day is open.
        if (dayOpen)
        {
            await db.LeagueEntries
                .Where(entry => entry.UserId == userId && entry.Day == today && entry.Status != LeagueEntryStatus.Removed && entry.Status != LeagueEntryStatus.Withdrawn)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entry => entry.Status, LeagueEntryStatus.Withdrawn)
                    .SetProperty(entry => entry.Reason, (LeagueEntryReason?)null)
                    .SetProperty(entry => entry.UpdatedAt, now), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
