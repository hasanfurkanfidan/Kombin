using Kombin.Api.Common.Text;
using Kombin.Api.Features.League;

namespace Kombin.Api.Tests;

/// <summary>The defaults are the numbers of <c>docs/design/league.md</c> (section 14); change one there and here together.</summary>
public class LeagueOptionsTests
{
    [Fact]
    public void TheDefaultsAreTheDesignNotesNumbers()
    {
        var options = new LeagueOptions();

        Assert.False(options.Enabled);
        Assert.Equal(new TimeOnly(21, 0), options.DayClosesAt);
        Assert.Equal(18, options.MinAge);
        Assert.Equal((30, 10), (options.MaxPairsPerDay, options.VotesForPoints));
        Assert.Equal(TimeSpan.FromMilliseconds(600), options.MinDecisionTime);
        Assert.Equal((20, 0.5), (options.NewVoterVotes, options.NewVoterWeight));
        Assert.Equal((5, 200, 8), (options.MinGamesForWinRate, options.PairCandidates, options.MinComparisons));
        Assert.Equal((0.5, 0.5, 1e-6, 500), (options.PriorWins, options.PriorLosses, options.BtTolerance, options.BtMaxIterations));
        Assert.Equal((70, 15, 10, 5, 3), (options.Points.VotesMax, options.Points.Voting, options.Points.Worn, options.Points.Piece, options.Points.PieceDaysPerWeek));
        Assert.Equal(5, options.BestDays);
        Assert.Equal((30, 35, 5, 5, 15, 2), (options.GroupSize, options.GroupMaxSize, options.PromoteCount, options.RelegateCount, options.MinTierPlayers, options.IdleWeeksToRelegate));
        Assert.Equal((30, 7, 1, 2, 50), (options.RepeatExactDays, options.RepeatNearDays, options.RepeatNearMaxDifference, options.MinPieces, options.ModerationRetriesPerRun));
        Assert.Equal((30, 20), (options.EngineFill.MinPool, options.EngineFill.MaxPerRun));
        Assert.Equal((3, TimeSpan.FromHours(72), 20), (options.Reports.HideAt, options.Reports.MinAccountAge, options.Reports.MinCountedVotes));
        Assert.Equal((90, 30, 30, 75.0, 4), (options.StyleScore.WindowDays, options.StyleScore.MinRankedPerDay, options.StyleScore.MinDays, options.StyleScore.Threshold, options.StyleScore.ConsecutiveWeeks));
        Assert.Equal((7, 30, 90), (options.Retention.ImageDays, options.Retention.PairDays, options.Retention.EntryDays));
    }

    [Fact]
    public void TheDefaultsAreValid() => Assert.Empty(LeagueOptionsValidator.Problems(new LeagueOptions()));

    [Fact]
    public void TheVotePointsAreMoreThanTwiceTheUsagePoints()
    {
        // The rules: "Oy puanı her zaman kullanım puanının iki katından fazladır" (70 against 15 + 10 + 5).
        var points = new LeagueOptions().Points;

        Assert.True(points.VotesMax > 2 * (points.Voting + points.Worn + points.Piece));
        Assert.Equal(100, points.VotesMax + points.Voting + points.Worn + points.Piece);
    }

    [Fact]
    public void EveryBrokenRuleIsListedAtOnce()
    {
        var options = new LeagueOptions
        {
            MinAge = -1,
            VotesForPoints = 40,
            NewVoterWeight = 0,
            BtTolerance = 0,
            BestDays = 8,
            GroupMaxSize = 10,
            RepeatExactDays = 0,
            EngineFill = new LeagueEngineFill { MinPool = -1 },
            Reports = new LeagueReports { HideAt = 0 },
            StyleScore = new LeagueStyleScoreOptions { Threshold = 120 },
            Retention = new LeagueRetention { EntryDays = 30 },
        };

        var problems = LeagueOptionsValidator.Problems(options).ToList();

        Assert.Equal(11, problems.Count);
        Assert.False(new LeagueOptionsValidator().Validate(null, options).Succeeded);
    }
}

public class LeagueConsentTests
{
    /// <summary>Published texts are pinned: editing one in place fails here; add a new version instead (every player then accepts again).</summary>
    private static readonly Dictionary<string, string> PublishedHashes = new()
    {
        ["2026-11-v1"] = "dc1036cdda6e78db603b6634fed7e62d2dd4f3086ab7b167ece4bfe8cd979ba6",
    };

    [Fact]
    public void PublishedTextsNeverChange()
    {
        foreach (var version in LeagueConsent.Versions)
        {
            Assert.True(PublishedHashes.TryGetValue(version.Version, out var hash), $"Pin the hash of the new league consent version {version.Version} here.");
            Assert.Equal(hash, version.Hash);
        }
    }

    [Fact]
    public void VersionOneIsTheOwnersTextWithTheAgeDeclaration()
    {
        Assert.Same(LeagueConsent.Versions[^1], LeagueConsent.Current);
        Assert.Equal("2026-11-v1", LeagueConsent.Current.Version);
        Assert.Equal(
            "Katılırsan dolabındaki parçaların fotoğrafları diğer oyunculara gösterilir. Adın ve profilin görünmez. İstediğin gün kombinini çıkarabilir, ayarlardan ligden ayrılabilirsin. Katılarak 18 yaşından büyük olduğunu onaylarsın.",
            LeagueConsent.Current.Text);
        Assert.Equal(LeagueConsent.Versions.Count, LeagueConsent.Versions.Select(version => version.Version).Distinct().Count());
    }

    [Fact]
    public void EveryVersionKeepsToTheGlossary() =>
        Assert.All(LeagueConsent.Versions, version => Assert.Empty(CopyGlossary.Violations(version.Text)));
}

public class LeagueRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    // Strict: a known year blocks unless the player is certainly 18 (2008 may still be 17 in 2026, so it waits for 2027).
    [Theory]
    [InlineData(2026, true)]
    [InlineData(2016, true)]
    [InlineData(2009, true)]
    [InlineData(2008, true)]
    [InlineData(2007, false)]
    [InlineData(1990, false)]
    [InlineData(null, false)]
    public void ABirthYearBlocksUnlessThePlayerIsCertainlyEighteen(int? birthYear, bool underAge) =>
        Assert.Equal(underAge, LeagueRules.IsUnderAge(birthYear, Today, 18));

    [Fact]
    public void TheYearTurnsTheAnswer() =>
        Assert.Equal((true, false), (LeagueRules.IsUnderAge(2008, new DateOnly(2026, 12, 31), 18), LeagueRules.IsUnderAge(2008, new DateOnly(2027, 1, 1), 18)));

    [Fact]
    public void AMinimumAgeOfZeroChecksNothing() => Assert.False(LeagueRules.IsUnderAge(2020, Today, 0));
}
