using Kombin.Api.Common.Text;
using Kombin.Api.Features.League;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Features.Notification;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Tests;

/// <summary>The day's Bradley–Terry fit (<c>docs/design/league.md</c>, section 8): the virtual prior, a known fit, convergence, determinism.</summary>
public class BradleyTerryTests
{
    private static readonly LeagueOptions Options = new();

    private static BtFit Fit(int count, params BtGame[] games) =>
        BradleyTerry.Fit(count, games, Options.PriorWins, Options.PriorLosses, Options.BtTolerance, Options.BtMaxIterations);

    private static BtGame[] Wins(int winner, int loser, int times, double weight = 1) => [.. Enumerable.Repeat(new BtGame(winner, loser, weight), times)];

    /// <summary>
    /// Newman's iteration (what <see cref="BradleyTerry.Fit"/> runs) and the design note's minorization–maximization step (Hunter 2004) solve the
    /// same likelihood: on a fixed set of games, including an unbeaten outfit, a winless one and half-weight votes, the fit equals an MM fit run to
    /// convergence (strengths within 1e-4, the same order). The MM step here is the note's formula, written out independently of the production code.
    /// </summary>
    [Fact]
    public void NewmansFitIsTheMmFixedPoint()
    {
        BtGame[] games =
        [
            .. Wins(0, 1, 3), .. Wins(1, 0, 1), .. Wins(0, 2, 2, 0.5), .. Wins(2, 3, 4), .. Wins(3, 2, 1), .. Wins(1, 3, 2),
            .. Wins(4, 0, 1), .. Wins(4, 2, 2), .. Wins(5, 4, 1, 0.5), .. Wins(3, 5, 2), .. Wins(1, 5, 1), .. Wins(0, 5, 1),
        ];
        const int count = 6;

        var newman = Fit(count, games).Strengths;
        var mm = HunterMm(count, games, Options.PriorWins, Options.PriorLosses);

        for (var i = 0; i < count; i++)
        {
            Assert.True(Math.Abs(newman[i] - mm[i]) < 1e-4, $"outfit {i}: Newman {newman[i]}, MM {mm[i]}");
        }

        Assert.Equal(Enumerable.Range(0, count).OrderByDescending(i => mm[i]), Enumerable.Range(0, count).OrderByDescending(i => newman[i]));
    }

    /// <summary>π_i ← (W_i + a) / (Σ_j n_ij / (π_i + π_j) + (a + b) / (π_i + 1)), simultaneous, until nothing moves by 1e-13 on the log scale.</summary>
    private static double[] HunterMm(int count, BtGame[] games, double priorWins, double priorLosses)
    {
        var strengths = Enumerable.Repeat(1.0, count).ToArray();
        for (var iteration = 0; iteration < 1_000_000; iteration++)
        {
            var next = new double[count];
            var change = 0.0;
            for (var i = 0; i < count; i++)
            {
                var wins = games.Where(game => game.Winner == i).Sum(game => game.Weight) + priorWins;
                var denominator = games.Where(game => game.Winner == i || game.Loser == i)
                    .Sum(game => game.Weight / (strengths[i] + strengths[game.Winner == i ? game.Loser : game.Winner]))
                    + ((priorWins + priorLosses) / (strengths[i] + 1));
                next[i] = wins / denominator;
                change = Math.Max(change, Math.Abs(Math.Log(next[i]) - Math.Log(strengths[i])));
            }

            strengths = next;
            if (change < 1e-13)
            {
                return strengths;
            }
        }

        throw new InvalidOperationException("MM did not converge in a million iterations.");
    }

    [Fact]
    public void ThePriorKeepsAnUnbeatenOutfitFinite()
    {
        var fit = Fit(2, Wins(0, 1, 30));

        Assert.True(fit.Converged);
        Assert.True(double.IsFinite(fit.Strengths[0]) && fit.Strengths[0] > 1);
        Assert.True(fit.Strengths[1] is > 0 and < 1);
    }

    /// <summary>
    /// One win of A over B, solved by hand: by symmetry π_B = 1 / π_A, and π_A = x solves x² / (x² + 1) + x / (x + 1) = 1.5, so x ≈ 2.1304. The
    /// fit also satisfies every outfit's score equation W_i + ½ = Σ_j n_ij π_i / (π_i + π_j) + π_i / (π_i + 1).
    /// </summary>
    [Fact]
    public void OneWinGivesTheHandSolvedStrengths()
    {
        var fit = Fit(2, new BtGame(0, 1, 1));

        Assert.Equal(2.1304, fit.Strengths[0], 3);
        Assert.Equal(1.0, fit.Strengths[0] * fit.Strengths[1], 5);
    }

    [Fact]
    public void AKnownThreeOutfitFitSatisfiesTheScoreEquations()
    {
        // A beats B twice and C once, B beats C three times and A once, C beats A once; weights include the half weight of new voters.
        BtGame[] games = [.. Wins(0, 1, 2), new(0, 2, 1), .. Wins(1, 2, 3, 0.5), new(1, 0, 1), new(2, 0, 1)];

        var fit = Fit(3, games);
        var π = fit.Strengths;

        Assert.True(fit.Converged);
        for (var i = 0; i < 3; i++)
        {
            var wins = games.Where(game => game.Winner == i).Sum(game => game.Weight) + Options.PriorWins;
            var expected = games.Where(game => game.Winner == i || game.Loser == i)
                .Sum(game => game.Weight * π[i] / (π[i] + π[game.Winner == i ? game.Loser : game.Winner]))
                + ((Options.PriorWins + Options.PriorLosses) * π[i] / (π[i] + 1));
            Assert.Equal(wins, expected, 4);
        }

        Assert.True(π[0] > π[1] && π[1] > π[2], string.Join(", ", π));
    }

    [Fact]
    public void ItStopsAtTheToleranceOrTheIterationCap()
    {
        var games = Wins(0, 1, 5).Concat(Wins(1, 2, 5)).Concat(Wins(2, 3, 5)).ToArray();

        var converged = Fit(4, games);
        var capped = BradleyTerry.Fit(4, games, 0.5, 0.5, 1e-6, maxIterations: 3);

        Assert.True(converged.Converged);
        Assert.InRange(converged.Iterations, 2, Options.BtMaxIterations - 1);
        Assert.False(capped.Converged);
        Assert.Equal(3, capped.Iterations);
    }

    /// <summary>
    /// A pure chain of wins (0 beat 1, 1 beat 2, 2 beat 3, five times each) is where Newman's step done simultaneously (Jacobi) oscillates and
    /// never converges; done in place it converges in a few dozen sweeps, in the right order.
    /// </summary>
    [Fact]
    public void APureChainOfWinsConvergesInTheRightOrder()
    {
        var fit = Fit(4, [.. Wins(0, 1, 5), .. Wins(1, 2, 5), .. Wins(2, 3, 5)]);

        Assert.True(fit.Converged, $"{fit.Iterations} iterations");
        Assert.True(fit.Iterations < 100, $"{fit.Iterations} iterations");
        Assert.True(fit.Strengths[0] > fit.Strengths[1] && fit.Strengths[1] > fit.Strengths[2] && fit.Strengths[2] > fit.Strengths[3], string.Join(", ", fit.Strengths));
    }

    /// <summary>A simulated day of 650 outfits and 15,000 votes (the design note's 1,000-player estimate) converges well within the 500 sweeps.</summary>
    [Fact]
    public void ASimulatedDayConvergesWithinTheCap()
    {
        var random = new Random(1);
        var quality = Enumerable.Range(0, 650).Select(_ => random.NextDouble() * 4 - 2).ToArray();
        var games = new List<BtGame>();
        for (var vote = 0; vote < 15_000; vote++)
        {
            var (a, b) = (random.Next(650), random.Next(650));
            if (a != b)
            {
                games.Add(random.NextDouble() < 1 / (1 + Math.Exp(quality[b] - quality[a])) ? new BtGame(a, b, 1) : new BtGame(b, a, 1));
            }
        }

        var fit = Fit(650, [.. games]);

        Assert.True(fit.Converged && fit.Iterations < 300, $"{fit.Iterations} iterations");
    }

    [Fact]
    public void TheSameGamesInAnyOrderGiveTheSameDoublesToTheLastBit()
    {
        var random = new Random(4);
        var games = Enumerable.Range(0, 400).Select(_ =>
        {
            var winner = random.Next(40);
            var loser = (winner + 1 + random.Next(39)) % 40;
            return new BtGame(winner, loser, random.Next(2) == 0 ? 0.5 : 1);
        }).ToArray();

        var first = Fit(40, games);
        var shuffled = Fit(40, [.. games.OrderBy(_ => random.Next())]);
        var again = Fit(40, games);

        Assert.Equal(first.Strengths.Select(BitConverter.DoubleToInt64Bits), shuffled.Strengths.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(first.Strengths.Select(BitConverter.DoubleToInt64Bits), again.Strengths.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(first.Iterations, shuffled.Iterations);
    }

    [Fact]
    public void TwoAudiencesThatNeverMetLandOnOneScale()
    {
        // Women's outfits 0 and 1, men's outfits 2 and 3: the same record in each group gives the same strength, thanks to the shared prior.
        var fit = Fit(4, [.. Wins(0, 1, 6), .. Wins(1, 0, 2), .. Wins(2, 3, 6), .. Wins(3, 2, 2)]);

        Assert.Equal(fit.Strengths[0], fit.Strengths[2], 9);
        Assert.Equal(fit.Strengths[1], fit.Strengths[3], 9);
        Assert.True(fit.Strengths[0] > 1 && fit.Strengths[1] < 1);
    }

    [Fact]
    public void AnOutfitWithoutVotesStaysAtTheAverage()
    {
        var fit = Fit(3, Wins(0, 1, 4));

        Assert.Equal(1.0, fit.Strengths[2], 12);
    }
}

/// <summary>The ranking and the percentile (<c>docs/design/league.md</c>, section 8, steps 3 and 4).</summary>
public class PercentileTests
{
    private static readonly Guid[] Ids = [.. Enumerable.Range(1, 6).Select(index => new Guid($"00000000-0000-0000-0000-00000000000{index}"))];

    [Fact]
    public void TheBestIsOneMinusHalfOverNAndTheWorstHalfOverN()
    {
        var ranked = Percentile.Rank([(Ids[0], 0.5), (Ids[1], 3.0), (Ids[2], 1.0), (Ids[3], 2.0)]);

        Assert.Equal([Ids[1], Ids[3], Ids[2], Ids[0]], ranked.Select(outfit => outfit.EntryId));
        Assert.Equal([1, 2, 3, 4], ranked.Select(outfit => outfit.Rank));
        Assert.Equal([0.875, 0.625, 0.375, 0.125], ranked.Select(outfit => outfit.Value));
    }

    [Fact]
    public void EqualStrengthsShareTheRankAndTheAveragePosition()
    {
        // Positions 2 and 3 tie: both rank 2, both p = (4 − 2.5 + 0.5) / 4 = 0.5; the order between them is the entry ID.
        var ranked = Percentile.Rank([(Ids[4], 2.0), (Ids[0], 3.0), (Ids[2], 2.0 * (1 + 1e-12)), (Ids[5], 1.0)]);

        Assert.Equal([Ids[0], Ids[2], Ids[4], Ids[5]], ranked.Select(outfit => outfit.EntryId));
        Assert.Equal([1, 2, 2, 4], ranked.Select(outfit => outfit.Rank));
        Assert.Equal([0.875, 0.5, 0.5, 0.125], ranked.Select(outfit => outfit.Value));
    }

    [Fact]
    public void AnEmptyDayRanksNothing() => Assert.Empty(Percentile.Rank([]));
}

/// <summary>The daily points and the week (<c>docs/design/league.md</c>, section 9; the rules, sections 4 and 5).</summary>
public class DailyPointsTests
{
    private static readonly LeagueOptions Options = new();

    private static RankedOutfit Best(int n) => Percentile.Rank([.. Enumerable.Range(0, n).Select(index => (Guid.NewGuid(), (double)(n - index)))])[0];

    private static DayPoints Points(RankedOutfit? ranked = null, int votes = 0, bool worn = false, bool piece = false, int pieceDaysBefore = 0) =>
        DailyPoints.For(new DailyActivity(ranked, votes, worn, piece, pieceDaysBefore), Options.Points, Options.VotesForPoints);

    [Fact]
    public void TheRulesExampleWeekIsBestFiveOfSeven() =>
        Assert.Equal(367, DailyPoints.WeekPoints([74, 0, 92, 61, 0, 85, 55], Options.BestDays));

    [Fact]
    public void AFullDayIsAtMostAHundred()
    {
        var points = Points(Best(70), votes: 10, worn: true, piece: true);

        Assert.Equal((70, 15, 10, 5, 100), (points.Votes, points.Voting, points.Worn, points.Piece, points.Total));
    }

    /// <summary>
    /// <c>round(70 × p)</c> half away from zero, p of the best outfit = (N − 0.5) / N, so the best gets round(70 − 35 / N): 66 at N = 8, 69 up to
    /// N = 69, 70 from N = 70 (69.5 rounds up). The design note's "70 only from N = 36" holds for a ceiling, not for this rounding.
    /// </summary>
    [Theory]
    [InlineData(1, 35)]
    [InlineData(8, 66)]
    [InlineData(35, 69)]
    [InlineData(36, 69)]
    [InlineData(69, 69)]
    [InlineData(70, 70)]
    [InlineData(500, 70)]
    public void TheBestOutfitsVotePoints(int rankedCount, int expected) => Assert.Equal(expected, Points(Best(rankedCount)).Votes);

    [Fact]
    public void TheMiddleOutfitGetsThirtyFiveAndTheRoundingIsExact()
    {
        // N = 3: the middle has p = 0.5 exactly; 70 × 0.5 = 35. N = 4, position 2: p = 0.625, 43.75 → 44.
        var three = Percentile.Rank([(Guid.NewGuid(), 3.0), (Guid.NewGuid(), 2.0), (Guid.NewGuid(), 1.0)]);
        var four = Percentile.Rank([(Guid.NewGuid(), 4.0), (Guid.NewGuid(), 3.0), (Guid.NewGuid(), 2.0), (Guid.NewGuid(), 1.0)]);

        Assert.Equal(35, Points(three[1]).Votes);
        Assert.Equal(44, Points(four[1]).Votes);
        Assert.Equal(DailyPoints.VotePoints(139, 140, 70), Points(Best(70)).Votes);
    }

    [Fact]
    public void AnUnrankedOutfitEarnsNoVotePoints() => Assert.Equal(0, Points(null, votes: 30, worn: true).Votes);

    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 15)]
    [InlineData(30, 15)]
    public void TenCountedVotesEarnTheVotingPoints(int counted, int expected) => Assert.Equal(expected, Points(votes: counted).Voting);

    [Theory]
    [InlineData(0, 5)]
    [InlineData(2, 5)]
    [InlineData(3, 0)]
    public void APieceCountsOnAtMostThreeDaysOfAWeek(int earlierDays, int expected) => Assert.Equal(expected, Points(piece: true, pieceDaysBefore: earlierDays).Piece);
}

/// <summary>The league calendar (beside <c>LeagueDaysTests</c>): weeks, the piece window, the close's catch-up window; every boundary is 21:00:00 Istanbul, half-open.</summary>
public class LeagueCalendarTests
{
    private static readonly TimeOnly Close = new(21, 0);

    [Theory]
    [InlineData("2026-11-16", "2026-11-16")]
    [InlineData("2026-11-18", "2026-11-16")]
    [InlineData("2026-11-22", "2026-11-16")]
    [InlineData("2026-11-23", "2026-11-23")]
    public void AWeekStartsOnMonday(string day, string monday) =>
        Assert.Equal(DateOnly.Parse(monday, System.Globalization.CultureInfo.InvariantCulture), LeagueDays.WeekStart(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void TwentyOneHundredIsEighteenHundredUtc()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 18, 0, 0, TimeSpan.Zero), LeagueDays.ClosesAt(new DateOnly(2026, 11, 17), Close));
        Assert.Equal((new DateTimeOffset(2026, 11, 16, 18, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 17, 18, 0, 0, TimeSpan.Zero)), LeagueDays.PieceWindow(new DateOnly(2026, 11, 17), Close));
    }

    [Fact]
    public void TheCloseWindowTurnsAtTwentyOneHundredSharp()
    {
        var day = new DateOnly(2026, 11, 17);
        var close = LeagueDays.ClosesAt(day, Close);

        Assert.Equal(day.AddDays(-1), LeagueDays.CloseWindow(close.AddTicks(-1), Close, 7)[^1]);
        Assert.Equal(day, LeagueDays.CloseWindow(close, Close, 7)[^1]);
        Assert.Equal([day.AddDays(-6), day.AddDays(-5), day.AddDays(-4), day.AddDays(-3), day.AddDays(-2), day.AddDays(-1), day], LeagueDays.CloseWindow(close.AddHours(2), Close, 7));
    }
}

/// <summary>The close's rules apart from the database (<c>DayCloser.Compute</c>): who is in the fit, who is ranked, who gets a row.</summary>
public class DayCloseRulesTests
{
    private static readonly DateOnly Day = new(2040, 1, 1);

    private static readonly LeagueOptions Options = new();

    private sealed class Pool
    {
        public List<CloseEntry> Entries { get; } = [];

        public List<ClosePair> Pairs { get; } = [];

        public HashSet<Guid> LeftOut { get; } = [];

        public Dictionary<Guid, int> Counted { get; } = [];

        public HashSet<Guid> Worn { get; } = [];

        public Guid Human(LeagueEntryStatus status = LeagueEntryStatus.Competing, int reports = 0, Guid? owner = null)
        {
            var entry = new CloseEntry(Guid.CreateVersion7(), owner ?? Guid.CreateVersion7(), LeagueEntryKind.Human, status, reports, Audience.Women, null, [Guid.NewGuid()]);
            Entries.Add(entry);
            return entry.Id;
        }

        public Guid Engine()
        {
            var entry = new CloseEntry(Guid.CreateVersion7(), null, LeagueEntryKind.Engine, LeagueEntryStatus.Competing, 0, Audience.Men, null, [Guid.NewGuid(), Guid.NewGuid()]);
            Entries.Add(entry);
            return entry.Id;
        }

        public void Vote(Guid winner, Guid loser, int times = 1, LeaguePairChoice choice = LeaguePairChoice.Left, double weight = 1, Guid? voter = null)
        {
            for (var index = 0; index < times; index++)
            {
                Pairs.Add(choice == LeaguePairChoice.Right
                    ? new ClosePair(voter ?? Guid.NewGuid(), loser, winner, choice, weight)
                    : new ClosePair(voter ?? Guid.NewGuid(), winner, loser, choice, weight));
            }
        }

        public Guid OwnerOf(Guid entryId) => Entries.Single(entry => entry.Id == entryId).UserId!.Value;

        public CloseOutcome Close() => DayCloser.Compute(
            new CloseInput(Day, Entries, Pairs, LeftOut, [.. Entries.Where(entry => entry.UserId is not null).Select(entry => entry.UserId!.Value).Order()], Counted, Worn, new HashSet<Guid>(), new Dictionary<Guid, int>()),
            Options);
    }

    private static LeagueDailyScore Row(CloseOutcome outcome, Guid owner) => outcome.Scores.Single(score => score.UserId == owner);

    [Fact]
    public void RankingNeedsEightCountedVotesOfThePairRows()
    {
        var pool = new Pool();
        var (a, b, c) = (pool.Human(), pool.Human(), pool.Human());
        pool.Vote(a, b, 4);
        pool.Vote(b, a, 4);
        pool.Vote(c, a, 3);

        var outcome = pool.Close();

        // a met 11 votes, b 8, c only 3: c competed but is not ranked.
        Assert.Equal(2, outcome.Ranked.Count);
        Assert.True(Row(outcome, pool.OwnerOf(a)).Ranked);
        Assert.Equal((true, false, 3), (Row(outcome, pool.OwnerOf(c)).Competed, Row(outcome, pool.OwnerOf(c)).Ranked, Row(outcome, pool.OwnerOf(c)).Comparisons));
        Assert.Equal(2, Row(outcome, pool.OwnerOf(c)).RankedCount);
    }

    [Fact]
    public void SkipsNeverCountEvenWhenMarkedCounted()
    {
        var pool = new Pool();
        var (a, b) = (pool.Human(), pool.Human());
        pool.Vote(a, b, 8, LeaguePairChoice.Skip);

        var outcome = pool.Close();

        Assert.Equal(0, outcome.Games);
        Assert.Empty(outcome.Ranked);
        Assert.Equal(0, Row(outcome, pool.OwnerOf(a)).Comparisons);
    }

    [Fact]
    public void VotesOnARemovedOutfitAreDroppedAndVotesOnAWithdrawnOneStay()
    {
        var pool = new Pool();
        var (a, b) = (pool.Human(), pool.Human());
        var removed = pool.Human(LeagueEntryStatus.Removed);
        var withdrawn = pool.Human(LeagueEntryStatus.Withdrawn);
        pool.Vote(a, b, 4);
        pool.Vote(b, a, 2);
        pool.Vote(a, removed, 4);
        pool.Vote(withdrawn, b, 4);

        var outcome = pool.Close();

        // a: 6 votes with b + 0 with the removed outfit = 6, not ranked; b: 6 with a + 4 with the withdrawn one = 10, ranked.
        Assert.Equal(10, outcome.Games);
        Assert.Equal(6, Row(outcome, pool.OwnerOf(a)).Comparisons);
        Assert.Equal((10, true), (Row(outcome, pool.OwnerOf(b)).Comparisons, Row(outcome, pool.OwnerOf(b)).Ranked));
        Assert.DoesNotContain(outcome.Strengths, row => row.EntryId == removed);
        Assert.Contains(outcome.Strengths, row => row.EntryId == withdrawn && row.Ranked is null);
        Assert.False(Row(outcome, pool.OwnerOf(withdrawn)).Competed);
        Assert.False(Row(outcome, pool.OwnerOf(removed)).Competed);
    }

    /// <summary>
    /// Task L5: an outfit hidden by reports and not reviewed is ranked like any other (reporting a rival must not wipe their vote points), its
    /// points are stored, and its row carries the under-review mark.
    /// </summary>
    [Fact]
    public void AnOutfitHiddenByReportsIsRankedWithItsPointsStoredAndMarkedUnderReview()
    {
        var pool = new Pool();
        var (a, b) = (pool.Human(), pool.Human());
        var hidden = pool.Human(reports: Options.Reports.HideAt);
        pool.Vote(a, hidden, 5);
        pool.Vote(hidden, b, 5);
        pool.Vote(a, b, 3);

        var outcome = pool.Close();
        var row = Row(outcome, pool.OwnerOf(hidden));

        Assert.Equal((true, true, 10, true), (row.Competed, row.Ranked, row.Comparisons, row.UnderReview));
        Assert.True(row.VotePoints > 0);
        Assert.Equal(row.VotePoints, row.Total);
        Assert.Equal(8, Row(outcome, pool.OwnerOf(a)).Comparisons);
        Assert.False(Row(outcome, pool.OwnerOf(a)).UnderReview);
        Assert.Equal(3, outcome.Ranked.Count);
    }

    [Fact]
    public void ALeftOutPlayersOutfitAndVotesAreNotInTheFit()
    {
        var pool = new Pool();
        var (a, b) = (pool.Human(), pool.Human());
        var disabled = pool.Human();
        var disabledOwner = pool.OwnerOf(disabled);
        pool.LeftOut.Add(disabledOwner);
        pool.Vote(a, b, 8);
        pool.Vote(a, disabled, 4);
        pool.Vote(b, a, 5, voter: disabledOwner);

        var outcome = pool.Close();

        Assert.Equal(8, outcome.Games);
        Assert.DoesNotContain(outcome.Strengths, row => row.EntryId == disabled);
    }

    [Fact]
    public void EngineOutfitsAreOpponentsAndTheirVotesBecomePreferencesWithoutAPlayersPieces()
    {
        var pool = new Pool();
        var a = pool.Human();
        var engine = pool.Engine();
        pool.Vote(a, engine, 6);
        pool.Vote(engine, a, 2, weight: 0.5);

        var outcome = pool.Close();

        Assert.DoesNotContain(outcome.Ranked, outfit => outfit.EntryId == engine);
        Assert.Equal(8, outcome.EnginePreferences.Count);
        Assert.Equal(6, outcome.EnginePreferences.Count(preference => preference.LoserProducts.Length == 2 && preference.WinnerProducts.Length == 0));
        Assert.All(outcome.EnginePreferences, preference => Assert.Equal(Audience.Men, preference.Audience));
        Assert.Equal(2, outcome.EnginePreferences.Count(preference => preference.WinnerProducts.Length == 2 && preference.Weight == 0.5f));
    }

    [Fact]
    public void AnyoneWithAnEntryOrAPointGetsARowAndNobodyElse()
    {
        var pool = new Pool();
        var notCompeting = pool.Human(LeagueEntryStatus.NotCompeting);
        var voter = Guid.NewGuid();
        var idle = Guid.NewGuid();
        pool.Counted[voter] = 12;
        pool.Worn.Add(voter);

        var outcome = DayCloser.Compute(
            new CloseInput(Day, pool.Entries, pool.Pairs, pool.LeftOut, [pool.OwnerOf(notCompeting), voter, idle], pool.Counted, pool.Worn, new HashSet<Guid>(), new Dictionary<Guid, int>()),
            Options);

        Assert.Equal(2, outcome.Scores.Count);
        Assert.Equal((false, 0), (Row(outcome, pool.OwnerOf(notCompeting)).Competed, Row(outcome, pool.OwnerOf(notCompeting)).Total));
        Assert.Equal((null, 25), (Row(outcome, voter).EntryId, Row(outcome, voter).Total));
    }

    [Fact]
    public void TheSameRowsGiveTheSameResultToTheLastBit()
    {
        var pool = new Pool();
        var ids = Enumerable.Range(0, 30).Select(_ => pool.Human()).ToList();
        var random = new Random(9);
        for (var vote = 0; vote < 600; vote++)
        {
            var (winner, loser) = (ids[random.Next(30)], ids[random.Next(30)]);
            if (winner != loser)
            {
                pool.Vote(winner, loser, weight: random.Next(3) == 0 ? 0.5 : 1);
            }
        }

        var first = pool.Close();
        var second = pool.Close();

        Assert.Equal(first.Strengths.Select(row => BitConverter.DoubleToInt64Bits(row.Strength)), second.Strengths.Select(row => BitConverter.DoubleToInt64Bits(row.Strength)));
        Assert.Equal(first.Scores.Select(score => (score.UserId, score.Rank, score.Percentile, score.Total)), second.Scores.Select(score => (score.UserId, score.Rank, score.Percentile, score.Total)));
    }
}

/// <summary>The evening result push (<c>docs/design/league.md</c>, section 14, the owner's lines); not sent before league PR 4.</summary>
public class LeagueResultCopyTests
{
    [Fact]
    public void TheTwoLinesAreTheOwnersText()
    {
        Assert.Equal(new NotificationText("Kombin Ligi sonuçları", "Bugünkü kombinin 3. oldu. Puanın 82."), Copy.ForLeagueResult(3, 82));
        Assert.Equal(new NotificationText("Kombin Ligi sonuçları", "Bugün yeterli oy toplanamadı. Puanın 25."), Copy.ForLeagueResult(null, 25));
    }

    [Fact]
    public void TheyKeepToTheGlossary()
    {
        foreach (var text in new[] { Copy.ForLeagueResult(1, 100), Copy.ForLeagueResult(null, 0) })
        {
            Assert.Empty(CopyGlossary.Violations(text.Title));
            Assert.Empty(CopyGlossary.Violations(text.Body));
        }
    }
}
