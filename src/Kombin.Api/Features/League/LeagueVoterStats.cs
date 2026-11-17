using Kombin.Api.Persistence;

namespace Kombin.Api.Features.League;

/// <summary>
/// A voter's day: pairs served and votes that counted. Served is the last sequence, the number the daily limit is decided from: a pair deleted
/// with another player's account still counts.
/// </summary>
internal sealed record VoterDay(int Served, int Counted);

internal static class LeagueVoterStats
{
    public static async Task<VoterDay> ForDayAsync(KombinDbContext db, Guid voterId, DateOnly day, CancellationToken cancellationToken)
    {
        var row = await db.LeaguePairs.AsNoTracking()
            .Where(pair => pair.VoterId == voterId && pair.Day == day)
            .GroupBy(_ => 1)
            .Select(group => new { Served = group.Max(pair => (int)pair.Sequence), Counted = group.Count(pair => pair.Counted) })
            .SingleOrDefaultAsync(cancellationToken);

        return new VoterDay(row?.Served ?? 0, row?.Counted ?? 0);
    }
}
