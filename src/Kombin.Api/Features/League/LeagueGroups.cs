using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League;

/// <summary>
/// Weeks and group membership as rows (design note section 10): the week a placement belongs to, the week row, and the mid-week placement of
/// players into the week's groups with a fresh alias (<see cref="GroupFormation.PlaceMidWeek"/>, <see cref="LeagueAliases.Draw"/>). Every
/// placement of a week runs under that week's transaction-scoped advisory lock, so two placements never pick the same alias or overfill a group.
/// </summary>
internal static class LeagueGroups
{
    /// <summary>
    /// The week a player who joins or plays at <paramref name="now"/> belongs to: the week of the next league day that is still open (after
    /// Sunday's 21:00 that is the next Monday's week).
    /// </summary>
    public static DateOnly PlacementWeek(DateTimeOffset now, TimeOnly closesAt)
    {
        var today = Istanbul.Today(now);

        return LeagueDays.WeekStart(now >= LeagueDays.ClosesAt(today, closesAt) ? today.AddDays(1) : today);
    }

    /// <summary>
    /// Whether the week before <paramref name="weekStart"/> is still waiting for its close: its row exists, is not closed, and the hourly job can still
    /// close it (<c>League:CloseCatchUpDays</c> after its end; a week stuck beyond that never blocks the next one). No row means nobody was ever in a
    /// group of that week (the first week of the league), so nothing is waiting. While it is pending the week close forms this week's groups at the
    /// tiers that week decides, and nobody is placed by hand (task B9).
    /// </summary>
    public static async Task<bool> PreviousWeekPendingAsync(
        KombinDbContext db, LeagueOptions options, DateOnly weekStart, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        var previous = weekStart.AddDays(-7);
        var row = await db.LeagueWeeks.AsNoTracking()
            .Where(week => week.WeekStart == previous)
            .Select(week => new { week.ClosedAt, week.EndsAt })
            .SingleOrDefaultAsync(cancellationToken);

        return row is { ClosedAt: null } && now < row.EndsAt.AddDays(options.CloseCatchUpDays);
    }

    /// <summary>When the week ends: Sunday's close.</summary>
    public static DateTimeOffset EndsAt(DateOnly weekStart, TimeOnly closesAt) => LeagueDays.ClosesAt(weekStart.AddDays(6), closesAt);

    /// <summary>Creates the week row when missing (no other effect). Inside a transaction or not.</summary>
    public static Task EnsureWeekAsync(KombinDbContext db, DateOnly weekStart, TimeOnly closesAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var endsAt = EndsAt(weekStart, closesAt);

        return db.Database.ExecuteSqlAsync(
            $"INSERT INTO league.weeks (week_start, ends_at) VALUES ({weekStart}, {endsAt}) ON CONFLICT (week_start) DO NOTHING", cancellationToken);
    }

    /// <summary>The transaction-scoped advisory lock of one week's groups. Must run inside a transaction.</summary>
    public static Task LockWeekAsync(KombinDbContext db, DateOnly weekStart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"league-week:{weekStart.DayNumber}"}, 0))", cancellationToken);
    }

    /// <summary>
    /// Places every given player who is a joined player and not yet in a group of <paramref name="weekStart"/> (which must not be closed), each in
    /// the group of their tier with the fewest members below <c>League:GroupMaxSize</c>, else in a new group of their tier, under an alias free in
    /// that group and different from their alias of the week before. Must run inside a transaction (it takes the week's lock). Returns the number
    /// placed. Nobody is placed while the week before is not closed (<see cref="PreviousWeekPendingAsync"/>): a placement then would use the tier of
    /// the week that is about to change it, and the close skips whoever is placed already.
    /// </summary>
    public static async Task<int> PlaceAsync(
        KombinDbContext db, LeagueOptions options, DateOnly weekStart, IReadOnlyCollection<Guid> userIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return 0;
        }

        await LockWeekAsync(db, weekStart, cancellationToken);
        await EnsureWeekAsync(db, weekStart, options.DayClosesAt, cancellationToken);
        if (await db.LeagueWeeks.AnyAsync(week => week.WeekStart == weekStart && week.ClosedAt != null, cancellationToken)
            || await PreviousWeekPendingAsync(db, options, weekStart, now, cancellationToken))
        {
            return 0;
        }

        var ids = userIds.Distinct().ToList();
        var players = await db.LeaguePlayers.AsNoTracking()
            .Where(player => ids.Contains(player.UserId) && player.LeftAt == null)
            .Where(player => !db.LeagueGroupMembers.Any(member => member.WeekStart == weekStart && member.UserId == player.UserId))
            .OrderBy(player => player.UserId)
            .Select(player => new { player.UserId, player.Tier })
            .ToListAsync(cancellationToken);
        if (players.Count == 0)
        {
            return 0;
        }

        var groups = await db.LeagueGroups.AsNoTracking()
            .Where(group => group.WeekStart == weekStart)
            .Select(group => new { group.Id, group.Tiers, Aliases = db.LeagueGroupMembers.Where(member => member.GroupId == group.Id).Select(member => member.Alias).ToList() })
            .ToListAsync(cancellationToken);
        var open = groups.ToDictionary(group => group.Id, group => (Tiers: (IReadOnlyList<LeagueTier>)group.Tiers, Aliases: group.Aliases.ToHashSet(StringComparer.Ordinal)));

        var previousWeek = weekStart.AddDays(-7);
        var playerIds = players.Select(player => player.UserId).ToList();
        var previous = await db.LeagueGroupMembers.AsNoTracking()
            .Where(member => member.WeekStart == previousWeek && playerIds.Contains(member.UserId))
            .ToDictionaryAsync(member => member.UserId, member => member.Alias, cancellationToken);

        foreach (var player in players)
        {
            var choice = GroupFormation.PlaceMidWeek([.. open.Select(group => new OpenGroup(group.Key, group.Value.Tiers, group.Value.Aliases.Count))], player.Tier, options.GroupMaxSize);
            if (choice is not { } groupId)
            {
                var group = new LeagueGroup { WeekStart = weekStart, Tiers = [player.Tier], Capacity = options.GroupSize };
                db.LeagueGroups.Add(group);
                groupId = group.Id;
                open[groupId] = ([player.Tier], new HashSet<string>(StringComparer.Ordinal));
            }

            var alias = LeagueAliases.Draw(open[groupId].Aliases, previous.GetValueOrDefault(player.UserId));
            open[groupId].Aliases.Add(alias);
            db.LeagueGroupMembers.Add(new LeagueGroupMember
            {
                WeekStart = weekStart,
                UserId = player.UserId,
                GroupId = groupId,
                Alias = alias,
                Tier = player.Tier,
                JoinedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return players.Count;
    }

    /// <summary><see cref="PlaceAsync"/> for one player in its own transaction (join, <c>GET /league/me</c>, <c>GET /league/table</c>).</summary>
    public static async Task PlaceOneAsync(KombinDbContext db, LeagueOptions options, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        var week = PlacementWeek(now, options.DayClosesAt);
        if (await db.LeagueGroupMembers.AnyAsync(member => member.WeekStart == week && member.UserId == userId, cancellationToken))
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PlaceAsync(db, options, week, [userId], now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
