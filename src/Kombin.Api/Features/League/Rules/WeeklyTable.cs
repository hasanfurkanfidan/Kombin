using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League.Rules;

/// <summary>A group member's week: their own tier, the daily totals of the closed days (at most seven) and the days they competed.</summary>
internal sealed record WeekRecord(Guid UserId, LeagueTier Tier, IReadOnlyList<int> DailyTotals, int CompetedDays);

/// <summary>A member's place: rank (1 = first, every rank distinct), week points (the best days), the seven-day sum, competed days.</summary>
internal sealed record WeekStanding(Guid UserId, LeagueTier Tier, int Rank, int Points, int Sum, int CompetedDays);

/// <summary>
/// The weekly table and its consequences (design note section 10; the rules, section 5). Pure.
/// <list type="bullet">
/// <item><b>Points</b>: the sum of the best <c>League:BestDays</c> (5) daily totals of the week (<see cref="DailyPoints.WeekPoints"/>).</item>
/// <item><b>Order</b>: points, then the seven-day sum, then competed days, then an FNV hash of (week, user): stable, not decided by sign-up order,
/// and the same on every read. Ranks are positions (no shared ranks), so "5. sıraya 17 puan" always names one row.</item>
/// <item><b>Bands</b>: among the members who competed on at least one day, the top <c>min(PromoteCount, floor(competed / 3))</c> go up and the
/// bottom <c>min(RelegateCount, floor(competed / 3))</c> go down (owner's decision: 5 each from 15 competing members, never overlapping). Diamond
/// does not go up and Bronze does not go down: they stay. Members who never competed "düşmez".</item>
/// <item><b>Idle</b>: a joined player without a competed day in a closed week gets one more idle week; at <c>League:IdleWeeksToRelegate</c> (2)
/// they go down one tier (Bronze stays) and the count restarts; a competed day resets it.</item>
/// </list>
/// </summary>
internal static class WeeklyTable
{
    public static IReadOnlyList<WeekStanding> Order(DateOnly weekStart, IEnumerable<WeekRecord> members, int bestDays)
    {
        ArgumentNullException.ThrowIfNull(members);

        return
        [
            .. members
                .Select(member => (Member: member, Points: DailyPoints.WeekPoints(member.DailyTotals, bestDays), Sum: member.DailyTotals.Sum()))
                .OrderByDescending(row => row.Points)
                .ThenByDescending(row => row.Sum)
                .ThenByDescending(row => row.Member.CompetedDays)
                .ThenBy(row => Tie(weekStart, row.Member.UserId))
                .ThenBy(row => row.Member.UserId)
                .Select((row, index) => new WeekStanding(row.Member.UserId, row.Member.Tier, index + 1, row.Points, row.Sum, row.Member.CompetedDays)),
        ];
    }

    /// <summary>How many go up and how many go down among <paramref name="competedMembers"/> members who competed.</summary>
    public static (int Promote, int Relegate) Bands(int competedMembers, int promoteCount, int relegateCount)
    {
        var cap = Math.Max(0, competedMembers) / 3;

        return (Math.Min(promoteCount, cap), Math.Min(relegateCount, cap));
    }

    /// <summary>Promoted, relegated or stayed, by the bands, for every member of one group in table order; idle members stay here.</summary>
    public static IReadOnlyDictionary<Guid, LeagueWeekOutcome> Outcomes(IReadOnlyList<WeekStanding> standings, int promoteCount, int relegateCount)
    {
        ArgumentNullException.ThrowIfNull(standings);

        var competed = standings.Where(standing => standing.CompetedDays > 0).ToList();
        var (up, down) = Bands(competed.Count, promoteCount, relegateCount);
        var outcomes = standings.ToDictionary(standing => standing.UserId, _ => LeagueWeekOutcome.Stayed);

        foreach (var standing in competed.Take(up).Where(standing => standing.Tier != LeagueTier.Diamond))
        {
            outcomes[standing.UserId] = LeagueWeekOutcome.Promoted;
        }

        foreach (var standing in competed.Skip(competed.Count - down).Where(standing => standing.Tier != LeagueTier.Bronze))
        {
            outcomes[standing.UserId] = LeagueWeekOutcome.Relegated;
        }

        return outcomes;
    }

    /// <summary>The idle count after a closed week, and whether the player goes down one tier for it (the count then restarts).</summary>
    public static (short IdleWeeks, bool Relegate) Idle(short idleWeeksBefore, bool competed, int idleWeeksToRelegate)
    {
        if (competed)
        {
            return (0, false);
        }

        var idle = idleWeeksBefore + 1;

        return idle >= idleWeeksToRelegate ? ((short)0, true) : ((short)idle, false);
    }

    public static LeagueTier Up(LeagueTier tier) => tier == LeagueTier.Diamond ? tier : tier + 1;

    public static LeagueTier Down(LeagueTier tier) => tier == LeagueTier.Bronze ? tier : tier - 1;

    public static LeagueTier Apply(LeagueTier tier, LeagueWeekOutcome outcome) => outcome switch
    {
        LeagueWeekOutcome.Promoted => Up(tier),
        LeagueWeekOutcome.Relegated or LeagueWeekOutcome.IdleRelegated => Down(tier),
        _ => tier,
    };

    /// <summary>FNV-1a (64 bit) over the week and the user: the last tie-break of the table and the order in which groups are cut.</summary>
    public static ulong Tie(DateOnly weekStart, Guid userId)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        Span<byte> bytes = stackalloc byte[20];
        BitConverter.TryWriteBytes(bytes[..4], weekStart.DayNumber);
        userId.TryWriteBytes(bytes[4..]);

        var hash = offset;
        foreach (var value in bytes)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }
}
