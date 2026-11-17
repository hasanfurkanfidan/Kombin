namespace Kombin.Api.Features.League.Rules;

/// <summary>
/// What one player did on one closed day. <c>Percentile</c>: the outfit's percentile when it was ranked, else null. <c>CountedVotes</c>: the
/// player's counted votes of the day (their own counter, <c>league.voter_days</c>). <c>Worn</c>: an outfit marked worn before the close.
/// <c>PieceAdded</c>: a qualifying wardrobe piece in the 21:00-to-21:00 window. <c>PieceDaysBefore</c>: earlier days of the same week that earned
/// piece points.
/// </summary>
internal sealed record DailyActivity(RankedOutfit? Percentile, int CountedVotes, bool Worn, bool PieceAdded, int PieceDaysBefore);

/// <summary>A day's points, each part on its own (at most 70 + 15 + 10 + 5 with the default settings).</summary>
internal sealed record DayPoints(int Votes, int Voting, int Worn, int Piece)
{
    public int Total => Votes + Voting + Worn + Piece;
}

/// <summary>
/// The daily points (design note section 9; the rules, section 4). Pure; every number is a <c>League:Points</c> setting.
/// <list type="bullet">
/// <item>Votes: <c>round(VotesMax × p)</c>, half away from zero, computed from the exact fraction of <see cref="Rules.Percentile"/> (a double
/// product could land a hair under a .5); 0 when the outfit was not ranked ("yeterli oy toplanamadı"). The best outfit of N gets
/// <c>round(70 − 35 / N)</c>: 66 at N = 8, 69 up to N = 69 and 70 from N = 70.</item>
/// <item>Voting: <c>Voting</c> when the day's counted votes reach <c>League:VotesForPoints</c>.</item>
/// <item>Worn: <c>Worn</c> for an outfit marked worn before the close (it need not be the competing one).</item>
/// <item>Piece: <c>Piece</c> for a piece added in the window, while fewer than <c>PieceDaysPerWeek</c> earlier days of the week earned it.</item>
/// </list>
/// </summary>
internal static class DailyPoints
{
    public static DayPoints For(DailyActivity activity, LeaguePoints points, int votesForPoints)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(points);

        return new DayPoints(
            activity.Percentile is { } ranked ? VotePoints(ranked.Numerator, ranked.Denominator, points.VotesMax) : 0,
            activity.CountedVotes >= votesForPoints ? points.Voting : 0,
            activity.Worn ? points.Worn : 0,
            activity.PieceAdded && activity.PieceDaysBefore < points.PieceDaysPerWeek ? points.Piece : 0);
    }

    /// <summary><c>round(max × numerator / denominator)</c>, half away from zero, in integers (the fraction is between 0 and 1).</summary>
    public static int VotePoints(long numerator, long denominator, int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        ArgumentOutOfRangeException.ThrowIfNegative(numerator);

        return (int)(((2L * max * numerator) + denominator) / (2L * denominator));
    }

    /// <summary>The week's points: the sum of the best <paramref name="bestDays"/> daily totals (missing days are 0).</summary>
    public static int WeekPoints(IEnumerable<int> dailyTotals, int bestDays) =>
        dailyTotals.OrderDescending().Take(bestDays).Sum();
}
