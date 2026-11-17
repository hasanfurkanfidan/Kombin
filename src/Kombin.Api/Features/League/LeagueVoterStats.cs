using Kombin.Api.Persistence;

namespace Kombin.Api.Features.League;

/// <summary>
/// A voter's day: pairs served and votes that counted, from the voter's own counter (<c>league.voter_days</c>). Served is the number the daily
/// limit and the next sequence are decided from; neither number shrinks when a pair is deleted with another player's account.
/// </summary>
internal sealed record VoterDay(int Served, int Counted);

internal static class LeagueVoterStats
{
    public static async Task<VoterDay> ForDayAsync(KombinDbContext db, Guid voterId, DateOnly day, CancellationToken cancellationToken)
    {
        var row = await db.LeagueVoterDays.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == voterId && row.Day == day, cancellationToken);

        return new VoterDay(row?.Served ?? 0, row?.Counted ?? 0);
    }
}
