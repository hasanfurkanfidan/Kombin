using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>Kombin Ligi, PR 2: pairing and voting (<c>docs/design/league.md</c>, sections 6, 7 and 13), each test on a league day of its own.</summary>
public sealed class LeagueVotingTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString();

    private Task<LeaguePair> PairRowAsync(Guid pairId) =>
        factory.WithDbAsync(db => db.LeaguePairs.AsNoTracking().SingleAsync(pair => pair.Id == pairId, Ct));

    private Task<LeagueEntry> EntryRowAsync(Guid entryId) =>
        factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking().SingleAsync(entry => entry.Id == entryId, Ct));

    private Task<int> SetCountedVotesAsync(Guid userId, int votes) =>
        factory.WithDbAsync(db => db.LeaguePlayers.Where(player => player.UserId == userId).ExecuteUpdateAsync(set => set.SetProperty(player => player.CountedVotes, votes), Ct));

    /// <summary>One player's outfit and <paramref name="engines"/> engine outfits: every pair a voter gets is that outfit against an engine one.</summary>
    private async Task<(TestUser Owner, Guid EntryId)> PoolAsync(DateOnly day, int engines)
    {
        var owner = await factory.CreatePlayerAsync(day, Ct);
        var entryId = await factory.InsertEntryAsync(day, owner.Id, cancellationToken: Ct);
        await factory.InsertEngineEntriesAsync(day, engines, Ct);

        return (owner, entryId);
    }

    [Fact]
    public async Task AVoterGetsThirtyPairsADayThenTheDayIsOverForThem()
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 16);
        using var _owner = owner;
        await factory.InsertEntryAsync(day, (await factory.CreatePlayerAsync(day, Ct)).Id, cancellationToken: Ct);
        using var voter = await factory.CreatePlayerAsync(day, Ct);

        var seen = new HashSet<(string, string)>();
        for (var sequence = 1; sequence <= 30; sequence++)
        {
            var pair = (await voter.Client.NextPairAsync(Ct)).Pair();
            Assert.Equal(sequence, pair.GetProperty("sequence").GetInt32());
            Assert.True(seen.Add(PairKey(pair)), $"pair {sequence} was shown before");
            await voter.Client.VoteOkAsync(pair.GetProperty("id").GetGuid(), sequence % 3 == 0 ? "skip" : "left", cancellationToken: Ct);
        }

        var over = await voter.Client.NextPairAsync(Ct);

        Assert.Equal(JsonValueKind.Null, over.GetProperty("pair").ValueKind);
        Assert.Equal("DailyLimit", over.GetProperty("reason").GetString());
        Assert.Equal((30, 30, 10), (over.GetProperty("servedToday").GetInt32(), over.GetProperty("limit").GetInt32(), over.GetProperty("target").GetInt32()));
        Assert.Equal(30, await factory.WithDbAsync(db => db.LeaguePairs.CountAsync(pair => pair.VoterId == voter.Id && pair.Day == day, Ct)));

        static (string, string) PairKey(JsonElement pair)
        {
            var sides = new[] { string.Join('|', pair.Images("left")), string.Join('|', pair.Images("right")) }.Order(StringComparer.Ordinal).ToArray();
            return (sides[0], sides[1]);
        }
    }

    [Fact]
    public async Task AskingAgainReturnsTheSamePairInsteadOfANewOne()
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 5);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);

        var first = (await voter.Client.NextPairAsync(Ct)).Pair();
        var again = (await voter.Client.NextPairAsync(Ct)).Pair();

        Assert.Equal(first.GetRawText(), again.GetRawText());
        Assert.Equal(1, await factory.WithDbAsync(db => db.LeaguePairs.CountAsync(pair => pair.VoterId == voter.Id, Ct)));
        Assert.Equal(1, (await voter.Client.NextPairAsync(Ct)).GetProperty("servedToday").GetInt32());
    }

    [Fact]
    public async Task TheVotersOwnOutfitIsNeverShownToThem()
    {
        var day = NewDay();
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        var own = await factory.InsertEntryAsync(day, voter.Id, cancellationToken: Ct);
        var (owner, other) = await PoolAsync(day, 6);
        using var _owner = owner;

        for (var round = 0; round < 6; round++)
        {
            var pair = (await voter.Client.NextPairAsync(Ct)).Pair();
            var row = await PairRowAsync(pair.GetProperty("id").GetGuid());
            Assert.DoesNotContain(own, new[] { row.LeftEntryId, row.RightEntryId });
            Assert.Contains(other, new[] { row.LeftEntryId, row.RightEntryId });
            await voter.Client.VoteOkAsync(row.Id, "skip", cancellationToken: Ct);
        }
    }

    [Fact]
    public async Task ATooFastVoteIsStoredButNotCounted()
    {
        var day = NewDay();
        var (owner, ownerEntry) = await PoolAsync(day, 6);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        var served = Time(day, 12);

        // Too fast by the server's clock (0.3 s), though the app says 1 s.
        voter.Client.At(served);
        var first = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();
        voter.Client.At(served.AddMilliseconds(300));
        var fast = await voter.Client.VoteOkAsync(first, "left", decisionMs: 1000, cancellationToken: Ct);

        // Too fast by the app's measure (0.3 s), though the server saw 5 s (a prefetched pair).
        voter.Client.At(served.AddSeconds(10));
        var second = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();
        voter.Client.At(served.AddSeconds(15));
        var tapped = await voter.Client.VoteOkAsync(second, "left", decisionMs: 300, cancellationToken: Ct);

        // Both at least 0.6 s: counted.
        voter.Client.At(served.AddSeconds(20));
        var third = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();
        voter.Client.At(served.AddSeconds(21));
        var counted = await voter.Client.VoteOkAsync(third, "left", decisionMs: 900, cancellationToken: Ct);

        Assert.Equal((false, false, true), (fast.GetProperty("counted").GetBoolean(), tapped.GetProperty("counted").GetBoolean(), counted.GetProperty("counted").GetBoolean()));
        Assert.Equal((0, 0, 1), (fast.GetProperty("countedToday").GetInt32(), tapped.GetProperty("countedToday").GetInt32(), counted.GetProperty("countedToday").GetInt32()));
        var rows = await factory.WithDbAsync(db => db.LeaguePairs.AsNoTracking().Where(pair => pair.VoterId == voter.Id).OrderBy(pair => pair.Sequence).ToListAsync(Ct));
        Assert.Equal(
            new[] { (false, 0f, LeaguePairChoice.Left, 1000), (false, 0f, LeaguePairChoice.Left, 300), (true, 0.5f, LeaguePairChoice.Left, 900) },
            rows.Select(pair => (pair.Counted, pair.Weight, pair.Choice!.Value, pair.ClientDecisionMilliseconds!.Value)));

        // The owner's outfit was in all three pairs, but only the counted vote moved its counters.
        Assert.Equal(1, (await EntryRowAsync(ownerEntry)).Comparisons);
        Assert.Equal(1, await factory.WithDbAsync(db => db.LeaguePlayers.Where(player => player.UserId == voter.Id).Select(player => player.CountedVotes).SingleAsync(Ct)));
    }

    [Fact]
    public async Task TheFirstTwentyCountedVotesWeighHalf()
    {
        var day = NewDay();
        var (owner, ownerEntry) = await PoolAsync(day, 4);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        await SetCountedVotesAsync(voter.Id, 19);

        var twentieth = await voter.Client.ServeAndLookAsync(Ct);
        var twentiethRow = await PairRowAsync(twentieth);
        var ownerSide = twentiethRow.LeftEntryId == ownerEntry ? "left" : "right";
        await voter.Client.VoteOkAsync(twentieth, ownerSide, cancellationToken: Ct);
        var twentyFirst = await voter.Client.ServeAndLookAsync(Ct);
        var otherSide = (await PairRowAsync(twentyFirst)).LeftEntryId == ownerEntry ? "right" : "left";
        await voter.Client.VoteOkAsync(twentyFirst, otherSide, cancellationToken: Ct);

        Assert.Equal((true, 0.5f), ((await PairRowAsync(twentieth)).Counted, (await PairRowAsync(twentieth)).Weight));
        Assert.Equal((true, 1f), ((await PairRowAsync(twentyFirst)).Counted, (await PairRowAsync(twentyFirst)).Weight));
        var entry = await EntryRowAsync(ownerEntry);
        Assert.Equal((2, 1.5, 0.5), (entry.Comparisons, entry.WeightedGames, entry.WeightedWins));
        var engine = await EntryRowAsync(twentiethRow.LeftEntryId == ownerEntry ? twentiethRow.RightEntryId : twentiethRow.LeftEntryId);
        Assert.Equal((1, 0.5, 0.0), (engine.Comparisons, engine.WeightedGames, engine.WeightedWins));
        Assert.Equal(21, await factory.WithDbAsync(db => db.LeaguePlayers.Where(player => player.UserId == voter.Id).Select(player => player.CountedVotes).SingleAsync(Ct)));
    }

    [Fact]
    public async Task TheVoteAnswersWithTheChosenOutfitsRealRecordIncludingThisVote()
    {
        var day = NewDay();
        var (owner, ownerEntry) = await PoolAsync(day, 3);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        await SetCountedVotesAsync(voter.Id, 100);

        // Before: 3 of 4 games won. This vote for it makes 4 of 5: 80 %.
        await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.Id == ownerEntry)
            .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.WeightedWins, 3.0).SetProperty(entry => entry.WeightedGames, 4.0).SetProperty(entry => entry.Comparisons, 4), Ct));
        var pair = await voter.Client.ServeAndLookAsync(Ct);
        var row = await PairRowAsync(pair);
        var forOwner = await voter.Client.VoteOkAsync(pair, row.LeftEntryId == ownerEntry ? "left" : "right", cancellationToken: Ct);

        Assert.Equal((true, 80, 5), (forOwner.GetProperty("counted").GetBoolean(), forOwner.GetProperty("chosenWinRate").GetInt32(), forOwner.GetProperty("chosenGames").GetInt32()));
        Assert.Equal((1, 1, 10, 30), (forOwner.GetProperty("countedToday").GetInt32(), forOwner.GetProperty("servedToday").GetInt32(), forOwner.GetProperty("target").GetInt32(), forOwner.GetProperty("limit").GetInt32()));

        // An engine outfit with one game: no rate yet ("İlk oylardan biri seninki"), the games say why.
        var next = await voter.Client.ServeAndLookAsync(Ct);
        var nextRow = await PairRowAsync(next);
        var forEngine = await voter.Client.VoteOkAsync(next, nextRow.LeftEntryId == ownerEntry ? "right" : "left", cancellationToken: Ct);
        Assert.Equal(JsonValueKind.Null, forEngine.GetProperty("chosenWinRate").ValueKind);
        Assert.Equal(1, forEngine.GetProperty("chosenGames").GetInt32());

        // A skip chose nothing.
        var skipped = await voter.Client.VoteOkAsync(await voter.Client.ServeAndLookAsync(Ct), "skip", cancellationToken: Ct);
        Assert.Equal((false, JsonValueKind.Null, JsonValueKind.Null), (skipped.GetProperty("counted").GetBoolean(), skipped.GetProperty("chosenWinRate").ValueKind, skipped.GetProperty("chosenGames").ValueKind));
    }

    [Fact]
    public async Task TheSameVoteAgainIsIdempotentAndAnotherChoiceIsAConflict()
    {
        var day = NewDay();
        var (owner, ownerEntry) = await PoolAsync(day, 3);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        var pair = await voter.Client.ServeAndLookAsync(Ct);

        var first = await voter.Client.VoteOkAsync(pair, "left", cancellationToken: Ct);
        var again = await voter.Client.VoteOkAsync(pair, "LEFT", cancellationToken: Ct);
        using var other = await voter.Client.VoteAsync(pair, "right", cancellationToken: Ct);
        using var skip = await voter.Client.VoteAsync(pair, "skip", cancellationToken: Ct);

        Assert.Equal(first.GetRawText(), again.GetRawText());
        Assert.Equal((HttpStatusCode.Conflict, "League.AlreadyVoted"), (other.StatusCode, await ErrorCodeAsync(other)));
        Assert.Equal((HttpStatusCode.Conflict, "League.AlreadyVoted"), (skip.StatusCode, await ErrorCodeAsync(skip)));
        Assert.Equal(1, (await EntryRowAsync(ownerEntry)).Comparisons);
        Assert.Equal(1, await factory.WithDbAsync(db => db.LeaguePlayers.Where(player => player.UserId == voter.Id).Select(player => player.CountedVotes).SingleAsync(Ct)));
    }

    [Fact]
    public async Task FromTheCloseOnVotesAndPairsAreRefused()
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 3);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        voter.Client.At(Time(day, 20, 59, 50));
        var pair = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();

        voter.Client.At(Time(day, 21));
        using var vote = await voter.Client.VoteAsync(pair, "left", cancellationToken: Ct);
        using var next = await voter.Client.GetAsync(NextPair, Ct);

        Assert.Equal((HttpStatusCode.Conflict, "League.DayClosed"), (vote.StatusCode, await ErrorCodeAsync(vote)));
        Assert.Equal((HttpStatusCode.Conflict, "League.DayClosed"), (next.StatusCode, await ErrorCodeAsync(next)));
        Assert.Null((await PairRowAsync(pair)).AnsweredAt);

        // A closed day stays closed whatever the clock says (the close of a later task writes closed_at).
        var closedDay = NewDay();
        var (closedOwner, _) = await PoolAsync(closedDay, 2);
        using var _closedOwner = closedOwner;
        await factory.WithDbAsync(async db =>
        {
            db.LeagueDays.Add(new LeagueDay { Day = closedDay, ClosesAt = Time(closedDay, 21), ClosedAt = Time(closedDay, 9) });
            return await db.SaveChangesAsync(Ct);
        });
        voter.Client.At(Time(closedDay, 10));
        using var early = await voter.Client.GetAsync(NextPair, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "League.DayClosed"), (early.StatusCode, await ErrorCodeAsync(early)));
    }

    [Fact]
    public async Task WithoutAnOutfitToShowTheAnswerIsNoPairs()
    {
        var day = NewDay();
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        await factory.InsertEngineEntriesAsync(day, 3, Ct);

        var next = await voter.Client.NextPairAsync(Ct);

        // Engine outfits alone are never paired with each other.
        Assert.Equal((JsonValueKind.Null, "NoPairs"), (next.GetProperty("pair").ValueKind, next.GetProperty("reason").GetString()));
        Assert.Equal(Time(day, 21), next.GetProperty("closesAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task OnlyAPlayerGetsPairsAndOnlyTheVoterUsesTheirPair()
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 3);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        using var intruder = await factory.CreatePlayerAsync(day, Ct);
        stranger.Client.At(Time(day, 12));
        var pair = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();

        using var notJoined = await stranger.Client.GetAsync(NextPair, Ct);
        using var vote = await intruder.Client.VoteAsync(pair, "left", cancellationToken: Ct);
        using var report = await intruder.Client.ReportOutfitAsync(pair, "left", cancellationToken: Ct);
        using var unknown = await voter.Client.VoteAsync(Guid.NewGuid(), "left", cancellationToken: Ct);

        Assert.Equal((HttpStatusCode.Forbidden, "League.NotJoined"), (notJoined.StatusCode, await ErrorCodeAsync(notJoined)));
        Assert.Equal((HttpStatusCode.NotFound, "League.PairNotFound"), (vote.StatusCode, await ErrorCodeAsync(vote)));
        Assert.Equal((HttpStatusCode.NotFound, "League.PairNotFound"), (report.StatusCode, await ErrorCodeAsync(report)));
        Assert.Equal((HttpStatusCode.NotFound, "League.PairNotFound"), (unknown.StatusCode, await ErrorCodeAsync(unknown)));
        Assert.Null((await PairRowAsync(pair)).AnsweredAt);
        Assert.False(await factory.WithDbAsync(db => db.LeagueReports.AnyAsync(row => row.ReporterId == intruder.Id, Ct)));
    }

    [Theory]
    [InlineData("middle", 1000)]
    [InlineData("", 1000)]
    [InlineData("1", 1000)]
    [InlineData("left", -1)]
    public async Task ABadVoteIsAValidationProblem(string choice, int decisionMs)
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 2);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        var pair = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();

        using var response = await voter.Client.VoteAsync(pair, choice, decisionMs, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await PairRowAsync(pair)).AnsweredAt);
    }

    [Fact]
    public async Task AReportedOutfitIsNeverShownToTheReporterAgainAndTheOpenPairIsUsedUp()
    {
        var day = NewDay();
        var (owner, ownerEntry) = await PoolAsync(day, 4);
        using var _owner = owner;
        await factory.InsertEntryAsync(day, (await factory.CreatePlayerAsync(day, Ct)).Id, exposures: 50, cancellationToken: Ct);
        using var reporter = await factory.CreatePlayerAsync(day, Ct);
        var pair = (await reporter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();
        var row = await PairRowAsync(pair);
        var side = row.LeftEntryId == ownerEntry ? "left" : "right";

        using var reported = await reporter.Client.ReportOutfitAsync(pair, side, "NotClothing", Ct);
        using var twice = await reporter.Client.ReportOutfitAsync(pair, side, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);
        Assert.True((await reported.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reported").GetBoolean());
        Assert.Equal((HttpStatusCode.Conflict, "League.AlreadyReported"), (twice.StatusCode, await ErrorCodeAsync(twice)));
        var used = await PairRowAsync(pair);
        Assert.Equal((LeaguePairChoice.Skip, false), (used.Choice, used.Counted));
        var report = await factory.WithDbAsync(db => db.LeagueReports.AsNoTracking().SingleAsync(row => row.ReporterId == reporter.Id, Ct));
        Assert.Equal((ownerEntry, LeagueReportReason.NotClothing), (report.EntryId, report.Reason));

        for (var round = 0; round < 4; round++)
        {
            var next = (await reporter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();
            var nextRow = await PairRowAsync(next);
            Assert.DoesNotContain(ownerEntry, new[] { nextRow.LeftEntryId, nextRow.RightEntryId });
            await reporter.Client.VoteOkAsync(next, "skip", cancellationToken: Ct);
        }
    }

    [Fact]
    public async Task OnlyATrustedReporterCountsAndEnoughCountingReportsHideTheOutfit()
    {
        var day = NewDay();
        var (owner, ownerEntry) = await PoolAsync(day, 3);
        using var _owner = owner;
        using var noVotes = await factory.CreatePlayerAsync(day, Ct);
        using var young = await factory.CreatePlayerAsync(day, Ct);
        using var trusted = await factory.CreatePlayerAsync(day, Ct);
        using var bystander = await factory.CreatePlayerAsync(day, Ct);

        // noVotes: an old account (at the day's clock) without counted votes; young: 20 counted votes, but the account is an hour old.
        await SetCountedVotesAsync(young.Id, 20);
        await SetCountedVotesAsync(trusted.Id, 20);
        await factory.WithDbAsync(db => db.Users.Where(user => user.Id == young.Id).ExecuteUpdateAsync(set => set.SetProperty(user => user.CreatedAt, Time(day, 11)), Ct));

        foreach (var reporter in new[] { noVotes, young, trusted })
        {
            var pair = (await reporter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();
            var row = await PairRowAsync(pair);
            using var response = await reporter.Client.ReportOutfitAsync(pair, row.LeftEntryId == ownerEntry ? "left" : "right", cancellationToken: Ct);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var counts = await factory.WithDbAsync(db => db.LeagueReports.AsNoTracking().Where(row => row.EntryId == ownerEntry).ToDictionaryAsync(row => row.ReporterId, row => row.Counts, Ct));
        Assert.Equal((false, false, true), (counts[noVotes.Id], counts[young.Id], counts[trusted.Id]));
        Assert.Equal(1, (await EntryRowAsync(ownerEntry)).CountingReports);
        Assert.NotEqual("NoPairs", (await bystander.Client.NextPairAsync(Ct)).GetProperty("reason").GetString());

        // League:Reports:HideAt (3) counting reports: nobody is shown the outfit any more (the bystander's open pair stays as it was served).
        await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.Id == ownerEntry).ExecuteUpdateAsync(set => set.SetProperty(entry => entry.CountingReports, 3), Ct));
        using var fresh = await factory.CreatePlayerAsync(day, Ct);
        Assert.Equal("NoPairs", (await fresh.Client.NextPairAsync(Ct)).GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("middle")]
    [InlineData("")]
    public async Task AReportNamesTheLeftOrTheRightOutfit(string side)
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 2);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        var pair = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();

        using var response = await voter.Client.ReportOutfitAsync(pair, side, cancellationToken: Ct);
        using var badReason = await voter.Client.ReportOutfitAsync(pair, "left", "Ugly", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badReason.StatusCode);
    }

    // Night review (task L3-fix), finding 1: engine outfits must never crowd every human outfit out of the candidate list.
    [Fact]
    public async Task TwelveBucketsOfThreePlayersAndTheirEngineFillHandOutEveryPair()
    {
        var day = NewDay();
        var buckets = (from audience in new[] { Audience.Women, Audience.Men }
                       from occasion in Enum.GetValues<Occasion>().Select(occasion => (Occasion?)occasion).Append(null)
                       select (audience, occasion)).ToList();
        Assert.Equal(12, buckets.Count);

        var players = new List<(TestUser User, Guid EntryId)>();
        foreach (var (audience, occasion) in buckets)
        {
            for (var index = 0; index < 3; index++)
            {
                var player = await factory.CreatePlayerAsync(day, Ct);
                players.Add((player, await factory.InsertEntryAsync(day, player.Id, audience, occasion, cancellationToken: Ct)));
            }
        }

        // What league-fill leaves at League:EngineFill:MinPool 30: 27 engine outfits per bucket, 324 in all, more than League:PairCandidates.
        await factory.InsertEngineEntriesAsync(day, [.. buckets.SelectMany(bucket => Enumerable.Repeat(bucket, 27))], Ct);

        try
        {
            // Every player votes their 30 pairs, six at a time: 36 × 30 = 1,080 pairs.
            var empty = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Parallel.ForEachAsync(players, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = Ct }, async (player, cancellationToken) =>
            {
                for (var round = 0; round < 30; round++)
                {
                    var next = await player.User.Client.NextPairAsync(cancellationToken);
                    if (next.GetProperty("pair").ValueKind != JsonValueKind.Object)
                    {
                        empty.Add(next.GetRawText());
                        return;
                    }

                    await player.User.Client.VoteOkAsync(next.Pair().GetProperty("id").GetGuid(), "skip", cancellationToken: cancellationToken);
                }
            });

            Assert.True(empty.IsEmpty, $"{empty.Count} players ran out of pairs, e.g. {empty.FirstOrDefault()}");
            var served = await factory.WithDbAsync(db => db.LeaguePairs.AsNoTracking()
                .Where(pair => pair.Day == day)
                .Select(pair => new
                {
                    LeftHuman = db.LeagueEntries.Any(entry => entry.Id == pair.LeftEntryId && entry.Kind == LeagueEntryKind.Human),
                    RightHuman = db.LeagueEntries.Any(entry => entry.Id == pair.RightEntryId && entry.Kind == LeagueEntryKind.Human),
                })
                .ToListAsync(Ct));
            Assert.Equal(1080, served.Count);
            Assert.All(served, pair => Assert.True(pair.LeftHuman || pair.RightHuman, "two engine outfits were paired"));

            var humanIds = players.Select(player => player.EntryId).ToList();
            var exposures = await factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking().Where(entry => humanIds.Contains(entry.Id)).Select(entry => entry.Exposures).ToListAsync(Ct));
            Assert.Equal(36, exposures.Count);
            Assert.All(exposures, count => Assert.True(count >= 1, "a player's outfit was never shown"));
        }
        finally
        {
            players.ForEach(player => player.User.Dispose());
        }
    }

    // Finding 3: a pair deleted with an account leaves a gap in the voter's sequence; the next pair must not reuse a number.
    [Fact]
    public async Task AfterAnAccountIsDeletedItsViewersStillGetTheirNextPair()
    {
        var day = NewDay();
        var (deleted, deletedEntry) = await PoolAsync(day, 6);
        using var voter = await factory.CreatePlayerAsync(day, Ct);

        // Pair 1 shows the outfit of the player who will go (the only human one); pair 2 a newer outfit nobody has seen.
        var first = await voter.Client.ServeAndLookAsync(Ct);
        await voter.Client.VoteOkAsync(first, "left", cancellationToken: Ct);
        using var other = await factory.CreatePlayerAsync(day, Ct);
        await factory.InsertEntryAsync(day, other.Id, cancellationToken: Ct);
        var second = await voter.Client.ServeAndLookAsync(Ct);
        var secondRow = await PairRowAsync(second);
        Assert.DoesNotContain(deletedEntry, new[] { secondRow.LeftEntryId, secondRow.RightEntryId });
        await voter.Client.VoteOkAsync(second, "left", cancellationToken: Ct);

        // The account is deleted mid-day: its entries cascade, and pair 1 with them.
        await factory.WithDbAsync(db => db.Users.Where(user => user.Id == deleted.Id).ExecuteDeleteAsync(Ct));
        deleted.Dispose();
        Assert.Equal(1, await factory.WithDbAsync(db => db.LeaguePairs.CountAsync(pair => pair.VoterId == voter.Id, Ct)));

        var next = await voter.Client.NextPairAsync(Ct);

        Assert.Equal(3, next.Pair().GetProperty("sequence").GetInt32());
        Assert.Equal(3, next.GetProperty("servedToday").GetInt32());
    }

    // Finding 4: an outfit of a player who left is never shown, even before the withdrawal reaches it.
    [Fact]
    public async Task AnOutfitOfAPlayerWhoLeftIsNeverShown()
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 6);
        using var _owner = owner;
        using var leaver = await factory.CreatePlayerAsync(day, Ct);
        var leaverEntry = await factory.InsertEntryAsync(day, leaver.Id, cancellationToken: Ct);
        await factory.WithDbAsync(db => db.LeaguePlayers.Where(player => player.UserId == leaver.Id).ExecuteUpdateAsync(set => set.SetProperty(player => player.LeftAt, DateTimeOffset.UtcNow), Ct));
        using var voter = await factory.CreatePlayerAsync(day, Ct);

        for (var round = 0; round < 6; round++)
        {
            var row = await PairRowAsync((await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid());
            Assert.DoesNotContain(leaverEntry, new[] { row.LeftEntryId, row.RightEntryId });
            await voter.Client.VoteOkAsync(row.Id, "skip", cancellationToken: Ct);
        }
    }

    // Finding 6: a report that uses up an open pair must not overwrite a vote that answered it in the meantime.
    [Fact]
    public async Task AReportNeverOverwritesAVoteThatAnsweredThePairMeanwhile()
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 4);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        var pairId = await voter.Client.ServeAndLookAsync(Ct);

        // A vote in flight holds the pair's row; the report arrives, finds the pair open and waits for the row.
        await using var vote = await factory.OpenConnectionAsync(Ct);
        await using (var transaction = await vote.BeginTransactionAsync(Ct))
        {
            await vote.ExecuteAsync("SELECT 1 FROM league.pairs WHERE id = @id FOR UPDATE", pairId, Ct);
            var report = voter.Client.ReportOutfitAsync(pairId, "left", cancellationToken: Ct);
            await factory.WaitForLockWaitAsync("pairs", Ct);
            await vote.ExecuteAsync("UPDATE league.pairs SET answered_at = now(), choice = 'Left', counted = true, weight = 1 WHERE id = @id", pairId, Ct);
            await transaction.CommitAsync(Ct);

            using var reported = await report;
            Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);
        }

        var row = await PairRowAsync(pairId);
        Assert.Equal((LeaguePairChoice.Left, true, 1f), (row.Choice, row.Counted, row.Weight));
    }

    // Finding 7: two votes on the same two outfits lock them in the same (ID) order, so they wait for each other instead of deadlocking.
    [Fact]
    public async Task AVoteLocksBothOutfitsInIdOrderSoTwoVotesOnThemNeverDeadlock()
    {
        var day = NewDay();
        var (owner, _) = await PoolAsync(day, 1);
        using var _owner = owner;
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        var pairId = await voter.Client.ServeAndLookAsync(Ct);
        var pair = await PairRowAsync(pairId);
        var ordered = await factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Id == pair.LeftEntryId || entry.Id == pair.RightEntryId)
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.Id)
            .ToListAsync(Ct));
        var (first, second) = (ordered[0], ordered[1]);

        // Another vote on the same two outfits is in flight: it has locked the first row (ID order) and is about to update the second. This
        // voter chooses the outfit whose row comes second.
        Exception? deadlock = null;
        await using var other = await factory.OpenConnectionAsync(Ct);
        await using (var transaction = await other.BeginTransactionAsync(Ct))
        {
            await other.ExecuteAsync("SELECT 1 FROM league.entries WHERE id = @id FOR UPDATE", first, Ct);
            var vote = voter.Client.VoteAsync(pairId, pair.LeftEntryId == second ? "left" : "right", cancellationToken: Ct);
            await factory.WaitForLockWaitAsync("entries", Ct);

            try
            {
                await other.ExecuteAsync("UPDATE league.entries SET comparisons = comparisons WHERE id = @id", second, Ct);
                await transaction.CommitAsync(Ct);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                deadlock = exception;
            }

            using var response = await vote;
            Assert.Null(deadlock);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal((1, 0.5, 0.5), ((await EntryRowAsync(second)).Comparisons, (await EntryRowAsync(second)).WeightedGames, (await EntryRowAsync(second)).WeightedWins));
        Assert.Equal((1, 0.5, 0.0), ((await EntryRowAsync(first)).Comparisons, (await EntryRowAsync(first)).WeightedGames, (await EntryRowAsync(first)).WeightedWins));
    }
}
