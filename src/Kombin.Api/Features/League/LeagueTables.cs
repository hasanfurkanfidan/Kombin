using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League;

/// <summary>One row of a group table as everyone in the group sees it: the place, the week points, whether it is the caller, the week's alias.</summary>
internal sealed record LeagueTableRow(int Rank, int Points, bool IsMe, string Alias);

/// <summary>
/// Where a week stands: <c>Open</c> (it is running), <c>Ending</c> (its last close has passed but the week close has not run: the table is as the day
/// closes left it, and the next week's groups are still to be formed) or <c>Final</c> (closed: the stored final table).
/// </summary>
internal enum LeagueWeekState
{
    Open,
    Ending,
    Final,
}

/// <summary>
/// The caller's group in one week: the rows (in table order), the caller's own place and points, and the promotion and relegation bands as they
/// stand. <see cref="PointsToPromotion"/>: the last promoted row's points minus the caller's, null when the caller is inside the band or there
/// is no band.
/// </summary>
internal sealed record MemberWeek(
    DateOnly WeekStart,
    DateTimeOffset EndsAt,
    LeagueWeekState State,
    LeagueTier Tier,
    IReadOnlyList<LeagueTableRow> Rows,
    int MyRank,
    int MyPoints,
    int PromoteCount,
    int RelegateCount)
{
    public int? PointsToPromotion => PromoteCount == 0 || MyRank <= PromoteCount ? null : Rows[PromoteCount - 1].Points - MyPoints;
}

/// <summary>
/// Reads a group table (design note sections 10 and 12). An open week is computed on read from the closed days so far (the live rank moves at
/// every 21:00 close and is never stored before Sunday); a closed week shows what its close stored, renumbered 1..n so an account deleted since
/// leaves no gap. Rows carry nothing but the place, the points, <c>isMe</c> and the per-week alias: no user ID, no name, nothing about an outfit.
/// </summary>
internal static class LeagueTables
{
    /// <summary>
    /// The week the caller sees at <paramref name="now"/>: the week of today, or, while the week before is waiting for its close and the caller has
    /// no group in today's week yet, that week as it stands (<see cref="LeagueWeekState.Ending"/>). Never a group of one made up for the gap.
    /// </summary>
    public static async Task<MemberWeek?> ForMemberNowAsync(KombinDbContext db, LeagueOptions options, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var weekStart = LeagueDays.WeekStart(Istanbul.Today(now));
        var view = await ForMemberAsync(db, options, userId, weekStart, now, cancellationToken);
        if (view is null && await LeagueGroups.PreviousWeekPendingAsync(db, options, weekStart, now, cancellationToken))
        {
            view = await ForMemberAsync(db, options, userId, weekStart.AddDays(-7), now, cancellationToken);
        }

        return view;
    }

    public static async Task<MemberWeek?> ForMemberAsync(
        KombinDbContext db, LeagueOptions options, Guid userId, DateOnly weekStart, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        var me = await db.LeagueGroupMembers.AsNoTracking().SingleOrDefaultAsync(member => member.WeekStart == weekStart && member.UserId == userId, cancellationToken);
        if (me is null)
        {
            return null;
        }

        var week = await db.LeagueWeeks.AsNoTracking().SingleAsync(row => row.WeekStart == weekStart, cancellationToken);
        var members = await db.LeagueGroupMembers.AsNoTracking()
            .Where(member => member.GroupId == me.GroupId)
            .Select(member => new { member.UserId, member.Alias, member.Tier, member.WeekPoints, member.FinalRank, member.CompetedDays })
            .ToListAsync(cancellationToken);

        List<(Guid UserId, int Points, string Alias)> ordered;
        int competed;
        if (week.ClosedAt is not null)
        {
            ordered = [.. members.OrderBy(member => member.FinalRank ?? int.MaxValue).ThenBy(member => member.UserId).Select(member => (member.UserId, member.WeekPoints ?? 0, member.Alias))];
            competed = members.Count(member => member.CompetedDays > 0);
        }
        else
        {
            var ids = members.Select(member => member.UserId).ToList();
            var weekEnd = weekStart.AddDays(6);
            var scores = (await db.LeagueDailyScores.AsNoTracking()
                .Where(score => ids.Contains(score.UserId) && score.Day >= weekStart && score.Day <= weekEnd)
                .OrderBy(score => score.Day)
                .Select(score => new { score.UserId, score.Total, score.Competed })
                .ToListAsync(cancellationToken))
                .ToLookup(score => score.UserId);
            var standings = WeeklyTable.Order(
                weekStart,
                members.Select(member => new WeekRecord(member.UserId, member.Tier, [.. scores[member.UserId].Select(score => score.Total)], scores[member.UserId].Count(score => score.Competed))),
                options.BestDays);
            var aliases = members.ToDictionary(member => member.UserId, member => member.Alias);
            ordered = [.. standings.Select(standing => (standing.UserId, standing.Points, aliases[standing.UserId]))];
            competed = standings.Count(standing => standing.CompetedDays > 0);
        }

        var rows = ordered.Select((row, index) => new LeagueTableRow(index + 1, row.Points, row.UserId == userId, row.Alias)).ToList();
        var mine = rows.Single(row => row.IsMe);
        var (promote, relegate) = WeeklyTable.Bands(competed, options.PromoteCount, options.RelegateCount);

        var state = week.ClosedAt is not null ? LeagueWeekState.Final : now >= week.EndsAt ? LeagueWeekState.Ending : LeagueWeekState.Open;

        return new MemberWeek(weekStart, week.EndsAt, state, me.Tier, rows, mine.Rank, mine.Points, promote, relegate);
    }
}
