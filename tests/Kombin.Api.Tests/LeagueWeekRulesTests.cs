using Kombin.Api.Common.Text;
using Kombin.Api.Features.League;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Tests;

/// <summary>The weekly table, its bands and idleness (<c>docs/design/league.md</c>, section 10; the rules, section 5).</summary>
public class WeeklyTableTests
{
    private static readonly DateOnly Monday = new(2026, 11, 16);

    private static readonly LeagueOptions Options = new();

    private static WeekRecord Member(int index, LeagueTier tier, int competed, params int[] totals) =>
        new(new Guid($"00000000-0000-0000-0000-{index:D12}"), tier, totals, competed);

    [Fact]
    public void WeekPointsAreTheBestFiveDaysAndTheOrderIsPointsThenSumThenCompetedDaysThenAStableHash()
    {
        var standings = WeeklyTable.Order(Monday,
        [
            Member(1, LeagueTier.Bronze, 5, 74, 0, 92, 61, 0, 85, 55),   // 367, sum 367
            Member(2, LeagueTier.Bronze, 5, 100, 100, 100, 67, 0),       // 367, sum 367, 5 competed
            Member(3, LeagueTier.Bronze, 7, 60, 60, 60, 60, 60, 10, 10), // 300, sum 320
            Member(4, LeagueTier.Bronze, 4, 61, 61, 61, 61, 56),         // 300, sum 300
            Member(5, LeagueTier.Bronze, 3, 100, 100, 100, 67, 0),       // 367, sum 367, 3 competed
        ], Options.BestDays);

        Assert.Equal([367, 367, 367, 300, 300], standings.Select(standing => standing.Points));
        Assert.Equal([1, 2, 3, 4, 5], standings.Select(standing => standing.Rank));
        Assert.Equal(new Guid("00000000-0000-0000-0000-000000000005"), standings[2].UserId);
        Assert.Equal(new Guid("00000000-0000-0000-0000-000000000003"), standings[3].UserId);

        // Members 1 and 2 tie on points, sum and competed days: the hash of (week, user) decides, the same way every time and in any input order.
        var reversed = WeeklyTable.Order(Monday, [Member(2, LeagueTier.Bronze, 5, 100, 100, 100, 67, 0), Member(1, LeagueTier.Bronze, 5, 74, 0, 92, 61, 0, 85, 55)], Options.BestDays);
        Assert.Equal(standings.Take(2).Select(standing => standing.UserId), reversed.Select(standing => standing.UserId));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(14, 4)]
    [InlineData(15, 5)]
    [InlineData(30, 5)]
    public void TheBandsAreMinFiveAndAThirdOfTheMembersWhoCompeted(int competed, int band) =>
        Assert.Equal((band, band), WeeklyTable.Bands(competed, Options.PromoteCount, Options.RelegateCount));

    [Fact]
    public void TheTopGoUpTheBottomGoDownDiamondAndBronzeStayAndTheIdleNeverFall()
    {
        var members = new List<WeekRecord>();
        for (var index = 1; index <= 15; index++)
        {
            var tier = index == 1 ? LeagueTier.Diamond : index == 15 ? LeagueTier.Bronze : LeagueTier.Gold;
            members.Add(Member(index, tier, 1, 100 - index));
        }

        members.Add(Member(16, LeagueTier.Gold, 0));
        members.Add(Member(17, LeagueTier.Gold, 0));

        var standings = WeeklyTable.Order(Monday, members, Options.BestDays);
        var outcomes = WeeklyTable.Outcomes(standings, Options.PromoteCount, Options.RelegateCount);

        LeagueWeekOutcome Of(int index) => outcomes[new Guid($"00000000-0000-0000-0000-{index:D12}")];
        Assert.Equal(LeagueWeekOutcome.Stayed, Of(1));
        Assert.All(Enumerable.Range(2, 4), index => Assert.Equal(LeagueWeekOutcome.Promoted, Of(index)));
        Assert.All(Enumerable.Range(6, 5), index => Assert.Equal(LeagueWeekOutcome.Stayed, Of(index)));
        Assert.All(Enumerable.Range(11, 4), index => Assert.Equal(LeagueWeekOutcome.Relegated, Of(index)));
        Assert.Equal(LeagueWeekOutcome.Stayed, Of(15));
        Assert.Equal((LeagueWeekOutcome.Stayed, LeagueWeekOutcome.Stayed), (Of(16), Of(17)));
    }

    [Fact]
    public void TwoIdleWeeksInARowCostOneTierAndACompetedDayResetsTheCount()
    {
        Assert.Equal(((short)1, false), WeeklyTable.Idle(0, competed: false, Options.IdleWeeksToRelegate));
        Assert.Equal(((short)0, true), WeeklyTable.Idle(1, competed: false, Options.IdleWeeksToRelegate));
        Assert.Equal(((short)0, false), WeeklyTable.Idle(1, competed: true, Options.IdleWeeksToRelegate));
        Assert.Equal(LeagueTier.Silver, WeeklyTable.Apply(LeagueTier.Gold, LeagueWeekOutcome.IdleRelegated));
        Assert.Equal(LeagueTier.Bronze, WeeklyTable.Apply(LeagueTier.Bronze, LeagueWeekOutcome.IdleRelegated));
        Assert.Equal(LeagueTier.Diamond, WeeklyTable.Apply(LeagueTier.Diamond, LeagueWeekOutcome.Promoted));
    }
}

/// <summary>Forming a week's groups and placing a mid-week joiner (<c>docs/design/league.md</c>, section 10).</summary>
public class GroupFormationTests
{
    private static readonly DateOnly Monday = new(2026, 11, 23);

    private static List<(Guid UserId, LeagueTier Tier)> Players(LeagueTier tier, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => (Guid.NewGuid(), tier))];

    [Fact]
    public void TiersBelowFifteenMergeUpwardAndTheTopRemainderJoinsTheSetBelow()
    {
        List<(Guid UserId, LeagueTier Tier)> players =
        [
            .. Players(LeagueTier.Bronze, 10), .. Players(LeagueTier.Silver, 8), .. Players(LeagueTier.Gold, 20),
            .. Players(LeagueTier.Platinum, 3), .. Players(LeagueTier.Diamond, 2),
        ];

        var groups = GroupFormation.Form(Monday, players, minTierPlayers: 15, groupSize: 30);

        Assert.Equal(2, groups.Count);
        Assert.Equal([LeagueTier.Bronze, LeagueTier.Silver], groups[0].Tiers);
        Assert.Equal(18, groups[0].Members.Count);
        Assert.Equal([LeagueTier.Gold, LeagueTier.Platinum, LeagueTier.Diamond], groups[1].Tiers);
        Assert.Equal(25, groups[1].Members.Count);
        Assert.Equal(players.Count, groups.SelectMany(group => group.Members).Distinct().Count());
    }

    [Fact]
    public void ALargeTierIsCutIntoNearEqualGroupsTheSameWayEveryTime()
    {
        var players = Players(LeagueTier.Bronze, 65);

        var groups = GroupFormation.Form(Monday, players, 15, 30);
        var again = GroupFormation.Form(Monday, [.. players.AsEnumerable().Reverse()], 15, 30);
        var nextWeek = GroupFormation.Form(Monday.AddDays(7), players, 15, 30);

        Assert.Equal([22, 22, 21], groups.Select(group => group.Members.Count));
        Assert.Equal(groups.Select(group => group.Members), again.Select(group => group.Members));
        Assert.NotEqual(groups[0].Members, nextWeek[0].Members);
    }

    [Fact]
    public void FewerThanFifteenPlayersInAllMakeOneGroup()
    {
        var groups = GroupFormation.Form(Monday, [.. Players(LeagueTier.Bronze, 4), .. Players(LeagueTier.Diamond, 3)], 15, 30);

        var group = Assert.Single(groups);
        Assert.Equal([LeagueTier.Bronze, LeagueTier.Diamond], group.Tiers);
    }

    [Fact]
    public void AMidWeekJoinerGoesToTheEmptiestGroupOfTheirTierBelowTheMaximum()
    {
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        OpenGroup[] groups =
        [
            new(a, [LeagueTier.Bronze], 20), new(b, [LeagueTier.Bronze, LeagueTier.Silver], 12), new(c, [LeagueTier.Silver], 35), new(d, [LeagueTier.Gold], 3),
        ];

        Assert.Equal(b, GroupFormation.PlaceMidWeek(groups, LeagueTier.Bronze, 35));
        Assert.Equal(b, GroupFormation.PlaceMidWeek(groups, LeagueTier.Silver, 35));
        Assert.Null(GroupFormation.PlaceMidWeek(groups, LeagueTier.Platinum, 35));
        Assert.Null(GroupFormation.PlaceMidWeek([new(c, [LeagueTier.Silver], 35)], LeagueTier.Silver, 35));
    }
}

/// <summary>The weekly aliases (<c>docs/design/league.md</c>, section 4): pinned word lists, unique in a group, new every week, nothing of the person.</summary>
public class LeagueAliasTests
{
    [Fact]
    public void TheWordListsArePinnedAndGiveEnoughAliases()
    {
        Assert.Equal(18, LeagueAliases.Colours.Count);
        Assert.Equal(16, LeagueAliases.Garments.Count);
        Assert.Equal(LeagueAliases.Colours.Count, LeagueAliases.Colours.Distinct().Count());
        Assert.Equal(LeagueAliases.Garments.Count, LeagueAliases.Garments.Distinct().Count());
        Assert.True(LeagueAliases.Count >= 225);
        Assert.Equal("Yeşil Trençkot", LeagueAliases.At((3 * 16) + 0));
        Assert.All(LeagueAliases.Colours.Concat(LeagueAliases.Garments), word => Assert.Empty(CopyGlossary.Violations(word)));
    }

    [Fact]
    public void AGroupOfThirtyFiveGetsThirtyFiveDifferentAliases()
    {
        for (var round = 0; round < 50; round++)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            for (var member = 0; member < 35; member++)
            {
                Assert.True(taken.Add(LeagueAliases.Draw(taken, null)));
            }
        }
    }

    [Fact]
    public void ThePreviousWeeksAliasIsNeverDrawnAgain()
    {
        var previous = LeagueAliases.At(7);

        // A random source that keeps answering the previous alias: the draw still moves on.
        Assert.NotEqual(previous, LeagueAliases.Draw(new HashSet<string>(), previous, _ => 7));
        for (var round = 0; round < 500; round++)
        {
            Assert.NotEqual(previous, LeagueAliases.Draw(new HashSet<string>(), previous));
        }
    }

    [Fact]
    public void AFullListEndsInsteadOfLooping()
    {
        var all = Enumerable.Range(0, LeagueAliases.Count).Select(LeagueAliases.At).ToHashSet(StringComparer.Ordinal);

        Assert.Throws<InvalidOperationException>(() => LeagueAliases.Draw(all, null));
    }
}

/// <summary>The style score and the league part of creator eligibility (<c>docs/design/league.md</c>, section 11; the rules, section 6).</summary>
public class StyleScoreTests
{
    private static readonly LeagueStyleScoreOptions Options = new();

    [Fact]
    public void TheScoreIsTheMeanLessTheStandardErrorTimesAHundred()
    {
        var result = StyleScore.Compute([0.9, 0.8, 0.7, 0.8]);

        // mean 0.8, sample sd = sqrt(0.02 / 3) = 0.0816, se = 0.0408 → 75.92.
        Assert.Equal(4, result.CountedDays);
        Assert.Equal(0.8, result.Mean!.Value, 10);
        Assert.Equal(Math.Sqrt(0.02 / 3), result.Deviation!.Value, 10);
        Assert.Equal(100 * (0.8 - (Math.Sqrt(0.02 / 3) / 2)), result.Score!.Value, 10);
    }

    [Fact]
    public void BelowTwoDaysThereIsNoScoreAndItNeverLeavesZeroToAHundred()
    {
        Assert.Null(StyleScore.Compute([0.95]).Score);
        Assert.Null(StyleScore.Compute([]).Score);
        Assert.Equal(0, StyleScore.Compute([0.0, 0.2]).Score);
        Assert.Equal(100, StyleScore.Compute([1.0, 1.0, 1.0]).Score);
    }

    [Fact]
    public void AWeekQualifiesFromThirtyDaysAtSeventyFiveAndFourInARowEarnTheRight()
    {
        var thirty = StyleScore.Compute([.. Enumerable.Repeat(0.8, 30)]);
        var twentyNine = StyleScore.Compute([.. Enumerable.Repeat(0.8, 29)]);
        var low = StyleScore.Compute([.. Enumerable.Repeat(0.7, 40)]);

        Assert.True(StyleScore.Qualifies(thirty, Options));
        Assert.False(StyleScore.Qualifies(twentyNine, Options));
        Assert.False(StyleScore.Qualifies(low, Options));

        Assert.Equal(((short)3, false), StyleScore.Next(2, true, false, Options.ConsecutiveWeeks));
        Assert.Equal(((short)4, true), StyleScore.Next(3, true, false, Options.ConsecutiveWeeks));
        Assert.Equal(((short)4, false), StyleScore.Next(3, true, removedRecently: true, Options.ConsecutiveWeeks));
        Assert.Equal(((short)0, false), StyleScore.Next(9, false, false, Options.ConsecutiveWeeks));
    }
}
