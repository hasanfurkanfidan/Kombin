using System.Diagnostics;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League;

internal enum WeekCloseOutcome
{
    Closed,
    AlreadyClosed,

    /// <summary>The week has not ended, or one of its seven days is not closed yet.</summary>
    NotReady,

    AlreadyRunning,
}

/// <summary>What a week close did.</summary>
internal sealed record WeekCloseResult(
    WeekCloseOutcome Outcome,
    DateOnly WeekStart,
    int Members,
    int Promoted,
    int Relegated,
    int IdleRelegated,
    int NextGroups,
    int NextMembers,
    int StyleScores,
    int Milliseconds)
{
    public static WeekCloseResult Nothing(WeekCloseOutcome outcome, DateOnly weekStart) => new(outcome, weekStart, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Closes one league week (design note sections 10 and 11), Monday 00:00 to Sunday 21:00 Istanbul, after its seven days are closed. One
/// transaction under <c>pg_try_advisory_xact_lock(0x4B4F4D42, 3)</c> (the day close holds object 2), the week row read <c>FOR UPDATE</c>; a closed
/// week is never closed again, and a close that dies half-way writes nothing.
/// <list type="number">
/// <item><b>The final tables</b>: per group, <see cref="WeeklyTable.Order"/> over each member's daily totals (stored at the day closes, so a
/// result removed by a review counts as it stands); every member gets <c>week_points</c>, <c>competed_days</c>, <c>final_rank</c> and an
/// <c>outcome</c> (<see cref="WeeklyTable.Outcomes"/>). A member whose account was deleted is simply not there (the rows cascaded).</item>
/// <item><b>Tiers</b>: the outcome moves each member from their own tier (labels are kept in merged groups); every joined player without a competed
/// day gets an idle week, and the second in a row takes them down one tier (<c>IdleRelegated</c>), group or not.</item>
/// <item><b>Next week's groups</b> (<see cref="GroupFormation.Form"/>): every joined player in good standing with a result row in the week, at the
/// new tier, not already placed there (a player placed mid-week keeps their group), each with a new alias (<see cref="LeagueAliases"/>, never the
/// alias of this week). The aliases are the one deliberately random part; everything else is decided by the rows in a fixed order.</item>
/// <item><b>Style scores</b> (<see cref="StyleScore"/>): for every player with a result in the 90 days before the week's end or a score row.</item>
/// </list>
/// The <c>players</c> argument of <see cref="CloseWeekAsync"/> limits every step to those players (tests: the database is shared); production
/// passes null.
/// </summary>
internal sealed partial class WeekCloser(KombinDbContext db, IOptions<LeagueOptions> options, ILogger<WeekCloser> logger)
{
    public const int LockObject = 3;

    public async Task<WeekCloseResult> CloseWeekAsync(DateOnly weekStart, DateTimeOffset now, IReadOnlyCollection<Guid>? players, CancellationToken cancellationToken)
    {
        if (weekStart.DayOfWeek != DayOfWeek.Monday)
        {
            throw new ArgumentException("A league week starts on a Monday.", nameof(weekStart));
        }

        var settings = options.Value;
        var stopwatch = Stopwatch.StartNew();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var locked = (await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({DayCloser.LockClass}, {LockObject}) AS \"Value\"").ToListAsync(cancellationToken)).Single();
        if (!locked)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WeekCloseResult.Nothing(WeekCloseOutcome.AlreadyRunning, weekStart);
        }

        await LeagueGroups.EnsureWeekAsync(db, weekStart, settings.DayClosesAt, cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.weeks WHERE week_start = {weekStart} FOR UPDATE", cancellationToken);
        var week = await db.LeagueWeeks.SingleAsync(row => row.WeekStart == weekStart, cancellationToken);
        if (week.ClosedAt is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return WeekCloseResult.Nothing(WeekCloseOutcome.AlreadyClosed, weekStart);
        }

        var weekEnd = weekStart.AddDays(6);
        var closedDays = await db.LeagueDays.CountAsync(day => day.Day >= weekStart && day.Day <= weekEnd && day.ClosedAt != null, cancellationToken);
        if (now < week.EndsAt || closedDays < 7)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WeekCloseResult.Nothing(WeekCloseOutcome.NotReady, weekStart);
        }

        // The week's results, per player, in day order.
        var scoreQuery = db.LeagueDailyScores.AsNoTracking().Where(score => score.Day >= weekStart && score.Day <= weekEnd);
        if (players is not null)
        {
            scoreQuery = scoreQuery.Where(score => players.Contains(score.UserId));
        }

        var scores = (await scoreQuery.OrderBy(score => score.UserId).ThenBy(score => score.Day).Select(score => new { score.UserId, score.Total, score.Competed }).ToListAsync(cancellationToken))
            .GroupBy(score => score.UserId)
            .ToDictionary(group => group.Key, group => (Totals: (IReadOnlyList<int>)[.. group.Select(score => score.Total)], Competed: group.Count(score => score.Competed)));

        // 1. The final tables and the band outcomes.
        var memberQuery = db.LeagueGroupMembers.Where(member => member.WeekStart == weekStart);
        if (players is not null)
        {
            memberQuery = memberQuery.Where(member => players.Contains(member.UserId));
        }

        var members = await memberQuery.OrderBy(member => member.GroupId).ThenBy(member => member.UserId).ToListAsync(cancellationToken);
        var outcomes = new Dictionary<Guid, LeagueWeekOutcome>();
        foreach (var group in members.GroupBy(member => member.GroupId))
        {
            var records = group.Select(member =>
            {
                return scores.TryGetValue(member.UserId, out var played)
                    ? new WeekRecord(member.UserId, member.Tier, played.Totals, played.Competed)
                    : new WeekRecord(member.UserId, member.Tier, [], 0);
            });
            var standings = WeeklyTable.Order(weekStart, records, settings.BestDays);
            var bands = WeeklyTable.Outcomes(standings, settings.PromoteCount, settings.RelegateCount);
            foreach (var standing in standings)
            {
                var member = group.Single(row => row.UserId == standing.UserId);
                member.WeekPoints = standing.Points;
                member.CompetedDays = (short)standing.CompetedDays;
                member.FinalRank = standing.Rank;
                member.Outcome = bands[standing.UserId];
                outcomes[standing.UserId] = bands[standing.UserId];
            }
        }

        // 2. Tiers: the band outcome, then idleness for every joined player.
        var playerQuery = db.LeaguePlayers.Where(player => player.LeftAt == null);
        if (players is not null)
        {
            playerQuery = playerQuery.Where(player => players.Contains(player.UserId));
        }

        var joined = await playerQuery.OrderBy(player => player.UserId).ToListAsync(cancellationToken);
        var membersByUser = members.ToDictionary(member => member.UserId);
        var (promoted, relegated, idleRelegated) = (0, 0, 0);
        foreach (var player in joined)
        {
            var competed = scores.TryGetValue(player.UserId, out var played) && played.Competed > 0;
            var (idleWeeks, idleDown) = WeeklyTable.Idle(player.IdleWeeks, competed, settings.IdleWeeksToRelegate);
            player.IdleWeeks = idleWeeks;

            var outcome = idleDown ? LeagueWeekOutcome.IdleRelegated : outcomes.GetValueOrDefault(player.UserId, LeagueWeekOutcome.Stayed);
            if (membersByUser.TryGetValue(player.UserId, out var member))
            {
                member.Outcome = outcome;
            }

            var tier = WeeklyTable.Apply(player.Tier, outcome);
            promoted += outcome == LeagueWeekOutcome.Promoted ? 1 : 0;
            relegated += outcome == LeagueWeekOutcome.Relegated ? 1 : 0;
            idleRelegated += outcome == LeagueWeekOutcome.IdleRelegated && tier != player.Tier ? 1 : 0;
            player.Tier = tier;
        }

        await db.SaveChangesAsync(cancellationToken);

        // 3. Next week's groups, for the players who played this week.
        var next = weekStart.AddDays(7);
        await LeagueGroups.LockWeekAsync(db, next, cancellationToken);
        await LeagueGroups.EnsureWeekAsync(db, next, settings.DayClosesAt, cancellationToken);
        var placed = await db.LeagueGroupMembers.AsNoTracking().Where(member => member.WeekStart == next).Select(member => member.UserId).ToListAsync(cancellationToken);
        var active = joined
            .Where(player => scores.ContainsKey(player.UserId) && !placed.Contains(player.UserId))
            .Select(player => player.UserId)
            .ToList();
        var inGoodStanding = await db.Users.AsNoTracking()
            .Where(user => active.Contains(user.Id) && user.DisabledAt == null && user.DeletionRequestedAt == null)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        var formable = joined.Where(player => inGoodStanding.Contains(player.UserId)).Select(player => (player.UserId, player.Tier)).ToList();
        var formed = GroupFormation.Form(next, formable, settings.MinTierPlayers, settings.GroupSize);
        foreach (var group in formed)
        {
            var row = new LeagueGroup { WeekStart = next, Tiers = [.. group.Tiers], Capacity = settings.GroupSize };
            db.LeagueGroups.Add(row);
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var userId in group.Members)
            {
                var alias = LeagueAliases.Draw(aliases, membersByUser.GetValueOrDefault(userId)?.Alias);
                aliases.Add(alias);
                db.LeagueGroupMembers.Add(new LeagueGroupMember
                {
                    WeekStart = next,
                    UserId = userId,
                    GroupId = row.Id,
                    Alias = alias,
                    Tier = formable.Single(player => player.UserId == userId).Tier,
                    JoinedAt = now,
                });
            }
        }

        await db.LeagueWeeks.Where(row => row.WeekStart == next).ExecuteUpdateAsync(set => set.SetProperty(row => row.GroupsFormedAt, now), cancellationToken);

        // 4. Style scores.
        var styleScores = await ComputeStyleScoresAsync(weekEnd, now, players, settings, cancellationToken);

        week.ClosedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var result = new WeekCloseResult(WeekCloseOutcome.Closed, weekStart, members.Count, promoted, relegated, idleRelegated, formed.Count,
            formed.Sum(group => group.Members.Count), styleScores, (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds));
        LogClosed(logger, weekStart, result.Members, promoted, relegated, idleRelegated, result.NextGroups, result.NextMembers, result.Milliseconds);

        return result;
    }

    private async Task<int> ComputeStyleScoresAsync(DateOnly weekEnd, DateTimeOffset now, IReadOnlyCollection<Guid>? players, LeagueOptions settings, CancellationToken cancellationToken)
    {
        var style = settings.StyleScore;
        var from = weekEnd.AddDays(-(style.WindowDays - 1));
        var window = db.LeagueDailyScores.AsNoTracking().Where(score => score.Day >= from && score.Day <= weekEnd);
        var existing = db.LeagueStyleScores.AsQueryable();
        if (players is not null)
        {
            window = window.Where(score => players.Contains(score.UserId));
            existing = existing.Where(score => players.Contains(score.UserId));
        }

        // Ranked, final days of days big enough to count; a result under review has no percentile to show yet.
        var days = (await window
            .Where(score => score.Ranked && !score.UnderReview && score.Percentile != null && score.RankedCount >= style.MinRankedPerDay)
            .OrderBy(score => score.UserId).ThenBy(score => score.Day)
            .Select(score => new { score.UserId, Percentile = score.Percentile!.Value })
            .ToListAsync(cancellationToken))
            .GroupBy(score => score.UserId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<double>)[.. group.Select(score => score.Percentile)]);
        var userIds = (await window.Select(score => score.UserId).Distinct().ToListAsync(cancellationToken))
            .Union(await existing.Select(score => score.UserId).ToListAsync(cancellationToken))
            .Order()
            .ToList();
        var rows = await existing.ToDictionaryAsync(score => score.UserId, cancellationToken);
        var removedFrom = weekEnd.AddDays(-(style.WindowDays - 1));
        var removed = (await db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.UserId != null && userIds.Contains(entry.UserId.Value) && entry.Status == LeagueEntryStatus.Removed && entry.Day >= removedFrom)
            .Select(entry => entry.UserId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        foreach (var userId in userIds)
        {
            var result = StyleScore.Compute(days.GetValueOrDefault(userId) ?? []);
            if (!rows.TryGetValue(userId, out var row))
            {
                row = new LeagueStyleScore { UserId = userId };
                db.LeagueStyleScores.Add(row);
            }

            var (weeks, eligible) = StyleScore.Next(row.QualifyingWeeks, StyleScore.Qualifies(result, style), removed.Contains(userId), style.ConsecutiveWeeks);
            row.ComputedAt = now;
            row.CountedDays = result.CountedDays;
            row.MeanPercentile = result.Mean;
            row.SdPercentile = result.Deviation;
            row.Score = result.Score;
            row.QualifyingWeeks = weeks;
            if (eligible && row.LeagueEligibleAt is null)
            {
                row.LeagueEligibleAt = now;
            }
        }

        return userIds.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "league-close: week {WeekStart} closed: {Members} members, {Promoted} up, {Relegated} down, {Idle} down for idleness; {Groups} groups of {Next} players formed for next week, {Milliseconds} ms.")]
    private static partial void LogClosed(ILogger logger, DateOnly weekStart, int members, int promoted, int relegated, int idle, int groups, int next, int milliseconds);
}
