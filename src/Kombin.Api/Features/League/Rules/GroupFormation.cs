using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League.Rules;

/// <summary>One group to form: the tiers it holds (one, or several merged) and its members, in the order they were cut.</summary>
internal sealed record FormedGroup(IReadOnlyList<LeagueTier> Tiers, IReadOnlyList<Guid> Members);

/// <summary>An existing group of a week, for a mid-week placement: its ID, its tiers and how many members it has.</summary>
internal sealed record OpenGroup(Guid Id, IReadOnlyList<LeagueTier> Tiers, int Members);

/// <summary>
/// The week's groups (design note section 10). Pure.
/// <list type="number">
/// <item>Per tier, from Bronze upward: a tier with fewer than <c>League:MinTierPlayers</c> (15) players is merged with the next tier up, and so on,
/// until the set has 15 or every tier is in it; a last set still under 15 (the top tiers) joins the set below it ("Diamond with the one below").
/// Every player keeps their own tier label.</item>
/// <item>Each set is cut into <c>ceil(n / League:GroupSize)</c> groups of near-equal size (differing by at most one), in the order of an FNV hash
/// of (week, user): random-looking but reproducible, and nobody can choose a group.</item>
/// <item>A player who joins or becomes active mid-week goes to the group holding their tier with the fewest members while it has fewer than
/// <c>League:GroupMaxSize</c> (35), else to a new group of their tier (<see cref="PlaceMidWeek"/>).</item>
/// </list>
/// </summary>
internal static class GroupFormation
{
    public static IReadOnlyList<FormedGroup> Form(DateOnly weekStart, IReadOnlyList<(Guid UserId, LeagueTier Tier)> players, int minTierPlayers, int groupSize)
    {
        ArgumentNullException.ThrowIfNull(players);

        var sets = new List<List<(Guid UserId, LeagueTier Tier)>>();
        var current = new List<(Guid UserId, LeagueTier Tier)>();
        foreach (var tier in Enum.GetValues<LeagueTier>())
        {
            current.AddRange(players.Where(player => player.Tier == tier));
            if (current.Count >= minTierPlayers)
            {
                sets.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            if (sets.Count > 0)
            {
                sets[^1].AddRange(current);
            }
            else
            {
                sets.Add(current);
            }
        }

        var groups = new List<FormedGroup>();
        foreach (var set in sets)
        {
            var ordered = set.OrderBy(player => WeeklyTable.Tie(weekStart, player.UserId)).ThenBy(player => player.UserId).ToList();
            var tiers = ordered.Select(player => player.Tier).Distinct().Order().ToList();
            var count = (ordered.Count + groupSize - 1) / groupSize;
            var (size, extra) = (ordered.Count / count, ordered.Count % count);
            var taken = 0;
            for (var index = 0; index < count; index++)
            {
                var length = size + (index < extra ? 1 : 0);
                groups.Add(new FormedGroup(tiers, [.. ordered.Skip(taken).Take(length).Select(player => player.UserId)]));
                taken += length;
            }
        }

        return groups;
    }

    /// <summary>The group a mid-week joiner of <paramref name="tier"/> goes to, or null for a new group of their tier.</summary>
    public static Guid? PlaceMidWeek(IReadOnlyList<OpenGroup> groups, LeagueTier tier, int groupMaxSize)
    {
        ArgumentNullException.ThrowIfNull(groups);

        return groups
            .Where(group => group.Tiers.Contains(tier) && group.Members < groupMaxSize)
            .OrderBy(group => group.Members)
            .ThenBy(group => group.Id)
            .Select(group => (Guid?)group.Id)
            .FirstOrDefault();
    }
}
