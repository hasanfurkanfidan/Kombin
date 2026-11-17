using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Identity;
using Kombin.Api.Features.League;
using Kombin.Api.Features.League.Jobs;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Kombin Ligi, PR 3: the day close (<c>docs/design/league.md</c>, sections 8 and 9), the results and the night review's requirements. Every test
/// plays days of its own (<see cref="LeagueClientExtensions.NewDay"/>), so the close of one never touches another's outfits; the classes that
/// close days run one after another (<see cref="LeagueCloses"/>).
/// </summary>
[Collection(LeagueCloses.Name)]
public sealed class LeagueCloseTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<List<LeagueDailyScore>> ScoresAsync(DateOnly day) =>
        factory.WithDbAsync(db => db.LeagueDailyScores.AsNoTracking().Where(row => row.Day == day).OrderBy(row => row.UserId).ToListAsync(Ct));

    private Task<LeagueDay?> DayRowAsync(DateOnly day) =>
        factory.WithDbAsync(db => db.LeagueDays.AsNoTracking().SingleOrDefaultAsync(row => row.Day == day, Ct));

    private Task<int> CountedVotesOnAsync(DateOnly day, Guid entryId) =>
        factory.WithDbAsync(db => db.LeaguePairs.CountAsync(
            pair => pair.Day == day && pair.Counted && pair.Weight > 0 && pair.Choice != LeaguePairChoice.Skip && (pair.LeftEntryId == entryId || pair.RightEntryId == entryId), Ct));

    private async Task<TestUser> WornPlayerAsync(DateOnly day)
    {
        var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);
        player.Client.At(Time(day, 13));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);

        return player;
    }

    [Fact]
    public async Task AFullDayOfTwelvePlayersAndTheEngineFillClosesOnceAndTheResultsShowOnlyTheCallersOwn()
    {
        var day = NewDay();
        var players = new List<TestUser>();
        for (var index = 0; index < 12; index++)
        {
            players.Add(await WornPlayerAsync(day));
        }

        await factory.RunLeagueEntriesAsync(Time(day, 13, 1), [.. players.Select(player => player.Id)], Ct);
        await factory.InsertEngineEntriesAsync(day, 6, Ct);
        var entries = await factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking().Where(entry => entry.Day == day).ToListAsync(Ct));
        Assert.Equal(12, entries.Count(entry => entry.Kind == LeagueEntryKind.Human && entry.Status == LeagueEntryStatus.Competing));

        // Each player answers up to 15 pairs: mostly counted votes, every seventh a skip, every eleventh too fast.
        var random = new Random(31);
        for (var round = 0; round < 15; round++)
        {
            for (var index = 0; index < players.Count; index++)
            {
                var voter = players[index].Client.At(Time(day, 14 + (round / 6), round % 6 * 10, index));
                var next = await voter.NextPairAsync(Ct);
                if (next.GetProperty("pair").ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var pairId = next.Pair().GetProperty("id").GetGuid();
                voter.Later(TimeSpan.FromSeconds(2));
                var serial = (round * 12) + index;
                var choice = serial % 7 == 0 ? "skip" : random.Next(2) == 0 ? "left" : "right";
                await voter.VoteOkAsync(pairId, choice, decisionMs: serial % 11 == 0 ? 200 : 1500, cancellationToken: Ct);
            }
        }

        using (var early = await factory.InternalCloseAsync(day.Iso(), Time(day, 20, 59, 59), Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.Equal("League.DayNotOver", (await early.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using var closed = await factory.InternalCloseAsync(day.Iso(), Time(day, 21, 0, 5), Ct);
        var body = await closed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(closed.StatusCode == HttpStatusCode.OK, body.GetRawText());
        Assert.True(body.GetProperty("closedNow").GetBoolean());

        var dayRow = (await DayRowAsync(day))!;
        var counted = await factory.WithDbAsync(db => db.LeaguePairs.CountAsync(pair => pair.Day == day && pair.Counted && pair.Choice != LeaguePairChoice.Skip, Ct));
        Assert.Equal(counted, dayRow.PairCount);
        Assert.Equal(6, dayRow.EngineCount);
        Assert.True(dayRow.BtConverged);
        Assert.NotNull(dayRow.ClosedAt);

        var scores = await ScoresAsync(day);
        Assert.Equal(12, scores.Count);
        var fitted = await factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking().Where(entry => entry.Day == day).ToDictionaryAsync(entry => entry.Id, Ct));
        foreach (var score in scores)
        {
            var entry = fitted[score.EntryId!.Value];
            var votes = await factory.WithDbAsync(db => db.LeagueVoterDays.Where(row => row.UserId == score.UserId && row.Day == day).Select(row => row.Counted).SingleAsync(Ct));

            Assert.True(score.Competed);
            Assert.Equal(await CountedVotesOnAsync(day, entry.Id), score.Comparisons);
            Assert.Equal(score.Comparisons >= 8, score.Ranked);
            Assert.Equal(10, score.WornPoints);
            Assert.Equal(votes >= 10 ? 15 : 0, score.VotingPoints);
            Assert.Equal(dayRow.RankedCount, score.RankedCount);
            Assert.NotNull(entry.Strength);
            Assert.Equal(score.Rank, entry.Rank);
            Assert.Equal(score.Percentile, entry.Percentile);
            Assert.Equal(score.Ranked ? (int)Math.Round(70m * (decimal)score.Percentile!.Value, MidpointRounding.AwayFromZero) : 0, score.VotePoints);
            Assert.Equal(score.VotePoints + score.VotingPoints + score.WornPoints + score.PiecePoints, score.Total);
        }

        Assert.Equal(dayRow.RankedCount, scores.Count(score => score.Ranked));
        Assert.All(fitted.Values.Where(entry => entry.Kind == LeagueEntryKind.Engine), entry => Assert.Null(entry.Rank));
        var engineIds = fitted.Values.Where(entry => entry.Kind == LeagueEntryKind.Engine).Select(entry => entry.Id).ToList();
        Assert.Equal(
            await factory.WithDbAsync(db => db.LeaguePairs.CountAsync(pair => pair.Day == day && pair.Counted && pair.Choice != LeaguePairChoice.Skip && (engineIds.Contains(pair.LeftEntryId) || engineIds.Contains(pair.RightEntryId)), Ct)),
            await factory.WithDbAsync(db => db.LeagueEnginePreferences.CountAsync(row => row.Day == day, Ct)));

        // Closing again changes nothing.
        var preferences = await factory.WithDbAsync(db => db.LeagueEnginePreferences.CountAsync(row => row.Day == day, Ct));
        using (var again = await factory.InternalCloseAsync(day.Iso(), Time(day, 22), Ct))
        {
            var repeated = await again.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.False(repeated.GetProperty("closedNow").GetBoolean());
            Assert.Equal(dayRow.RankedCount, repeated.GetProperty("rankedCount").GetInt32());
        }

        Assert.Equal(DayCloseOutcome.AlreadyClosed, (await factory.CloseDayAsync(day, Time(day, 23), Ct)).Outcome);
        Assert.Equivalent(scores, await ScoresAsync(day), strict: true);
        Assert.Equal(preferences, await factory.WithDbAsync(db => db.LeagueEnginePreferences.CountAsync(row => row.Day == day, Ct)));

        // The results: the caller's own entry and points, nothing of anyone else.
        var me = players[0];
        var mine = scores.Single(score => score.UserId == me.Id);
        using var results = await me.Client.ResultsAsync(day, Ct);
        var text = await results.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, results.StatusCode);
        var result = JsonDocument.Parse(text).RootElement;
        Assert.Equal(day.Iso(), result.GetProperty("day").GetString());
        Assert.Equal((mine.Competed, mine.Ranked, mine.Comparisons, mine.RankedCount), (result.GetProperty("competed").GetBoolean(), result.GetProperty("ranked").GetBoolean(), result.GetProperty("comparisons").GetInt32(), result.GetProperty("rankedCount").GetInt32()));
        Assert.Equal(mine.Total, result.GetProperty("points").GetProperty("total").GetInt32());
        Assert.Equal(3, result.GetProperty("entry").GetProperty("pieces").GetArrayLength());
        Assert.True(result.GetProperty("piecePointsAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("week").ValueKind);
        foreach (var other in players.Skip(1))
        {
            Assert.DoesNotContain(other.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var entry in fitted.Values.Where(entry => entry.UserId != me.Id))
        {
            Assert.DoesNotContain(entry.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.All(entry.Pieces, piece => Assert.DoesNotContain(piece.ImageKey ?? "-none-", text, StringComparison.Ordinal));
        }

        Assert.Equal(text, await (await me.Client.GetAsync("/api/v1/league/results/latest", Ct)).Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ResultsAreNotReadyBeforeTheCloseAndTheDayMustBeADate()
    {
        var day = NewDay();
        var player = await WornPlayerAsync(day);

        using (var early = await player.Client.ResultsAsync(day, Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);
            Assert.Equal("League.ResultsNotReady", (await early.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using (var latest = await player.Client.GetAsync("/api/v1/league/results/latest", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, latest.StatusCode);
        }

        foreach (var bad in new[] { "/api/v1/league/results", "/api/v1/league/results?day=17.11.2026", "/api/v1/league/results?day=2026-13-01" })
        {
            using var response = await player.Client.GetAsync(bad, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("League.InvalidDay", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using var internalBad = await factory.InternalCloseAsync("yesterday", Time(day, 22), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, internalBad.StatusCode);
    }

    /// <summary>
    /// 21:00:00 Istanbul is past the close, on every path: a vote at 20:59:59.999 counts, one at 21:00:00 is 409; a wear at 20:59:59.999 earns
    /// the worn points, one at 21:00:00 earns nothing that day (nor the next: the worn window is 00:00 to 21:00); a piece added at 20:59:59.999
    /// belongs to the day, one at 21:00:00 to the next.
    /// </summary>
    [Fact]
    public async Task TwentyOneHundredSharpBelongsToTheNextSideOfTheBoundary()
    {
        var day = NewDay();
        var next = day.AddDays(1);
        var owner = await factory.CreatePlayerAsync(day, Ct);
        var entryId = await factory.InsertEntryAsync(day, owner.Id, cancellationToken: Ct);
        await factory.InsertEngineEntriesAsync(day, 3, Ct);
        var voter = await factory.CreatePlayerAsync(day, Ct);

        voter.Client.At(Time(day, 20, 59, 58));
        var first = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();
        voter.Client.At(Time(day, 20, 59, 59, 999));
        Assert.True((await voter.Client.VoteOkAsync(first, "left", cancellationToken: Ct)).GetProperty("counted").GetBoolean());
        var second = (await voter.Client.NextPairAsync(Ct)).Pair().GetProperty("id").GetGuid();
        voter.Client.At(Time(day, 21));
        using (var late = await voter.Client.VoteAsync(second, "left", cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Equal("League.DayClosed", (await late.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using (var serve = await voter.Client.GetAsync(NextPair, Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, serve.StatusCode);
        }

        var beforeWearer = await factory.CreatePlayerAsync(day, Ct);
        var atWearer = await factory.CreatePlayerAsync(day, Ct);
        beforeWearer.Client.At(Time(day, 20, 59, 59, 999));
        await beforeWearer.Client.WearOutfitAsync((await factory.SeedOutfitAsync(beforeWearer.Id, Ct)).OutfitId, Ct);
        atWearer.Client.At(Time(day, 21));
        await atWearer.Client.WearOutfitAsync((await factory.SeedOutfitAsync(atWearer.Id, Ct)).OutfitId, Ct);
        await factory.RunLeagueEntriesAsync(Time(day, 21), [atWearer.Id], Ct);
        Assert.Null(await factory.EntryAsync(atWearer.Id, day, Ct));

        // Pieces: one a millisecond before the close, one on it.
        var beforePiece = await factory.CreatePlayerAsync(day, Ct);
        var atPiece = await factory.CreatePlayerAsync(day, Ct);
        await AddPieceAsync(beforePiece.Id, Time(day, 20, 59, 59, 999));
        await AddPieceAsync(atPiece.Id, Time(day, 21));

        Assert.Equal(DayCloseOutcome.NotOver, (await factory.CloseDayAsync(day, Time(day, 20, 59, 59, 999), Ct)).Outcome);
        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(day, Time(day, 21), Ct)).Outcome);
        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(next, Time(next, 21), Ct)).Outcome);

        var today = (await ScoresAsync(day)).ToDictionary(score => score.UserId);
        var tomorrow = (await ScoresAsync(next)).ToDictionary(score => score.UserId);
        Assert.Equal(1, await factory.WithDbAsync(db => db.LeagueVoterDays.Where(row => row.UserId == voter.Id && row.Day == day).Select(row => row.Counted).SingleAsync(Ct)));
        Assert.Equal(1, await CountedVotesOnAsync(day, entryId));
        Assert.Equal(10, today[beforeWearer.Id].WornPoints);
        Assert.False(today.ContainsKey(atWearer.Id));
        Assert.False(tomorrow.ContainsKey(atWearer.Id));
        Assert.Equal(5, today[beforePiece.Id].PiecePoints);
        Assert.False(today.ContainsKey(atPiece.Id));
        Assert.Equal(5, tomorrow[atPiece.Id].PiecePoints);
        Assert.False(tomorrow.ContainsKey(beforePiece.Id));
    }

    private Task<int> AddPieceAsync(Guid userId, DateTimeOffset createdAt) => factory.WithDbAsync(async db =>
    {
        var category = await db.Categories.Where(row => row.Slug == "ust-giyim/gomlek").Select(row => row.Id).SingleAsync(Ct);
        var item = new WardrobeItem { UserId = userId, CategoryId = category, Status = WardrobeItemStatus.Ready, ConfirmedAt = createdAt };
        db.WardrobeItems.Add(item);
        await db.SaveChangesAsync(Ct);

        return await db.WardrobeItems.Where(row => row.Id == item.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedAt, createdAt), Ct);
    });

    [Fact]
    public async Task ADayWithoutARowAndADayWhoseRowAVoteCreatedBothClose()
    {
        // No vote at all: no league.days row. The entry competed, nobody compared it: unranked, still a row with the usage points.
        var quiet = NewDay();
        var lonely = await WornPlayerAsync(quiet);
        await factory.RunLeagueEntriesAsync(Time(quiet, 13, 1), [lonely.Id], Ct);
        Assert.Null(await DayRowAsync(quiet));

        var result = await factory.CloseDayAsync(quiet, Time(quiet, 21, 0, 1), Ct);

        Assert.Equal((DayCloseOutcome.Closed, 0, 0), (result.Outcome, result.RankedCount, result.PairCount));
        var score = Assert.Single(await ScoresAsync(quiet));
        Assert.Equal((lonely.Id, true, false, 0, 10), (score.UserId, score.Competed, score.Ranked, score.Comparisons, score.Total));
        Assert.NotNull((await DayRowAsync(quiet))!.ClosedAt);

        // One vote created the row, open: the close treats it as open.
        var voted = NewDay();
        var owner = await factory.CreatePlayerAsync(voted, Ct);
        await factory.InsertEntryAsync(voted, owner.Id, cancellationToken: Ct);
        await factory.InsertEngineEntriesAsync(voted, 2, Ct);
        var voter = await factory.CreatePlayerAsync(voted, Ct);
        await voter.Client.VoteOkAsync(await voter.Client.ServeAndLookAsync(Ct), "left", cancellationToken: Ct);
        Assert.Null((await DayRowAsync(voted))!.ClosedAt);

        var closed = await factory.CloseDayAsync(voted, Time(voted, 21, 0, 1), Ct);

        Assert.Equal((DayCloseOutcome.Closed, 1), (closed.Outcome, closed.PairCount));
        Assert.Contains(await ScoresAsync(voted), row => row.UserId == owner.Id && row.Competed && row.Comparisons == 1);
    }

    /// <summary>
    /// The close reads the pair rows, not the entry counters: an entry whose counters say 50 but which has no counted row is not ranked, one whose
    /// counters say 0 but which has eight counted rows is. A skip is never a vote, whatever its <c>counted</c> flag says. Votes on a withdrawn
    /// outfit stay, votes on a removed one go, and an outfit hidden by reports is ranked with an under-review mark (task L5).
    /// </summary>
    [Fact]
    public async Task TheCloseCountsThePairRowsAndDecidesWithdrawnRemovedAndHiddenOutfits()
    {
        var day = NewDay();
        async Task<(Guid Owner, Guid Entry)> HumanAsync(Action<LeagueEntry>? change = null)
        {
            var player = await factory.CreatePlayerAsync(day, Ct);
            var entryId = await factory.InsertEntryAsync(day, player.Id, cancellationToken: Ct);
            if (change is not null)
            {
                await factory.WithDbAsync(async db =>
                {
                    var entry = await db.LeagueEntries.SingleAsync(row => row.Id == entryId, Ct);
                    change(entry);
                    return await db.SaveChangesAsync(Ct);
                });
            }

            return (player.Id, entryId);
        }

        var ranked = await HumanAsync();
        var counters = await HumanAsync(entry => (entry.Comparisons, entry.WeightedWins, entry.WeightedGames) = (50, 40, 50));
        var hidden = await HumanAsync(entry => entry.CountingReports = 3);
        var skipped = await HumanAsync();
        var withdrawn = await HumanAsync(entry => entry.Status = LeagueEntryStatus.Withdrawn);
        var removed = await HumanAsync(entry => entry.Status = LeagueEntryStatus.Removed);
        var engines = new List<Guid>();
        for (var index = 0; index < 8; index++)
        {
            engines.Add(await factory.InsertEntryAsync(day, null, cancellationToken: Ct));
        }

        using var voter = await factory.CreateUserAsync(Ct);
        await factory.InsertVotesAsync(day, voter.Id,
        [
            .. engines.Take(7).Select(engine => (ranked.Entry, engine, LeaguePairChoice.Left, true)),
            (ranked.Entry, withdrawn.Entry, LeaguePairChoice.Left, true),
            (ranked.Entry, removed.Entry, LeaguePairChoice.Right, true),
            .. engines.Select(engine => (hidden.Entry, engine, LeaguePairChoice.Right, true)),
            .. engines.Select(engine => (skipped.Entry, engine, LeaguePairChoice.Skip, true)),
        ], Ct);

        var result = await factory.CloseDayAsync(day, Time(day, 21, 30), Ct);

        Assert.Equal(DayCloseOutcome.Closed, result.Outcome);
        Assert.Equal(7 + 1 + 8, result.PairCount);
        var scores = (await ScoresAsync(day)).ToDictionary(score => score.UserId);
        Assert.Equal((true, true, 8), (scores[ranked.Owner].Competed, scores[ranked.Owner].Ranked, scores[ranked.Owner].Comparisons));
        Assert.Equal((true, false, 0), (scores[counters.Owner].Competed, scores[counters.Owner].Ranked, scores[counters.Owner].Comparisons));
        Assert.Equal((true, true, 8, true), (scores[hidden.Owner].Competed, scores[hidden.Owner].Ranked, scores[hidden.Owner].Comparisons, scores[hidden.Owner].UnderReview));
        Assert.Equal((2, 0.25), (scores[hidden.Owner].Rank, scores[hidden.Owner].Percentile));
        Assert.Equal((true, false, 0), (scores[skipped.Owner].Competed, scores[skipped.Owner].Ranked, scores[skipped.Owner].Comparisons));
        Assert.Equal((false, false, 1), (scores[withdrawn.Owner].Competed, scores[withdrawn.Owner].Ranked, scores[withdrawn.Owner].Comparisons));
        Assert.Equal((false, 0), (scores[removed.Owner].Competed, scores[removed.Owner].Comparisons));
        Assert.Equal((1, 2, 0.75, false), (scores[ranked.Owner].Rank, scores[ranked.Owner].RankedCount, scores[ranked.Owner].Percentile, scores[ranked.Owner].UnderReview));
        Assert.Equal(15, await factory.WithDbAsync(db => db.LeagueEnginePreferences.CountAsync(row => row.Day == day, Ct)));
        Assert.Null(await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.Id == removed.Entry).Select(entry => entry.Strength).SingleAsync(Ct)));
        Assert.NotNull(await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.Id == withdrawn.Entry).Select(entry => entry.Strength).SingleAsync(Ct)));
    }

    /// <summary>
    /// The voter's ten votes all showed one player's outfit; that player's account is deleted during the day, which deletes the pair rows. The
    /// voter's own counter keeps the ten, so the voting points are still earned, the served count does not shrink and no number is reused. After
    /// the close, deleting the voter's account removes their score and counter rows and leaves the bystander's row as it was.
    /// </summary>
    [Fact]
    public async Task AnAccountDeletedDuringTheDayCostsTheVotersNothingAndTheirOwnRowsGoWithTheirAccount()
    {
        var day = NewDay();
        var owner = await factory.CreatePlayerAsync(day, Ct);
        await factory.InsertEntryAsync(day, owner.Id, cancellationToken: Ct);
        await factory.InsertEngineEntriesAsync(day, 10, Ct);
        var voter = await factory.CreatePlayerAsync(day, Ct);
        var bystander = await factory.CreatePlayerAsync(day, Ct);

        for (var round = 0; round < 10; round++)
        {
            voter.Client.At(Time(day, 12, round));
            await voter.Client.VoteOkAsync(await voter.Client.ServeAndLookAsync(Ct), "left", cancellationToken: Ct);
        }

        bystander.Client.At(Time(day, 13));
        await bystander.Client.VoteOkAsync(await bystander.Client.ServeAndLookAsync(Ct), "left", cancellationToken: Ct);
        await AddPieceAsync(bystander.Id, Time(day, 13, 5));
        Assert.Equal(10, await factory.WithDbAsync(db => db.LeaguePairs.CountAsync(pair => pair.VoterId == voter.Id && pair.Day == day, Ct)));

        Assert.Equal(PurgeStatus.Purged, (await factory.RunJobAsync<AccountPurger, PurgeResult>(purger => purger.PurgeAsync(owner.Id, Ct))).Status);

        Assert.Equal(0, await factory.WithDbAsync(db => db.LeaguePairs.CountAsync(pair => pair.VoterId == voter.Id && pair.Day == day, Ct)));
        voter.Client.At(Time(day, 14));
        var today = (await voter.Client.GetJsonAsync(Me, Ct)).GetProperty("today");
        Assert.Equal((10, 10), (today.GetProperty("servedPairs").GetInt32(), today.GetProperty("countedVotes").GetInt32()));
        var next = await voter.Client.NextPairAsync(Ct);
        Assert.Equal((10, 10), (next.GetProperty("servedToday").GetInt32(), next.GetProperty("countedToday").GetInt32()));

        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(day, Time(day, 21, 1), Ct)).Outcome);
        var scores = (await ScoresAsync(day)).ToDictionary(score => score.UserId);
        Assert.Equal((15, 15, null), (scores[voter.Id].VotingPoints, scores[voter.Id].Total, scores[voter.Id].EntryId));
        Assert.Equal((0, 5), (scores[bystander.Id].VotingPoints, scores[bystander.Id].PiecePoints));
        Assert.False(scores.ContainsKey(owner.Id));

        var bystanderBefore = scores[bystander.Id];
        Assert.Equal(PurgeStatus.Purged, (await factory.RunJobAsync<AccountPurger, PurgeResult>(purger => purger.PurgeAsync(voter.Id, Ct))).Status);

        Assert.False(await factory.WithDbAsync(db => db.LeagueDailyScores.AnyAsync(row => row.UserId == voter.Id, Ct)));
        Assert.False(await factory.WithDbAsync(db => db.LeagueVoterDays.AnyAsync(row => row.UserId == voter.Id, Ct)));
        Assert.Equivalent(bystanderBefore, (await ScoresAsync(day)).Single(score => score.UserId == bystander.Id), strict: true);
        Assert.True(await factory.WithDbAsync(db => db.LeagueVoterDays.AnyAsync(row => row.UserId == bystander.Id && row.Day == day, Ct)));
    }

    /// <summary>The close writes <c>competed</c>, so the repeat rule finally has something to read: the outfit that competed today sits out tomorrow.</summary>
    [Fact]
    public async Task AnOutfitThatCompetedSitsOutTheNextDay()
    {
        var day = NewDay();
        var next = day.AddDays(1);
        var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);

        player.Client.At(Time(day, 13));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await factory.RunLeagueEntriesAsync(Time(day, 13, 1), [player.Id], Ct);
        Assert.Equal(LeagueEntryStatus.Competing, (await factory.EntryAsync(player.Id, day, Ct))!.Status);
        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(day, Time(day, 21), Ct)).Outcome);
        Assert.True((await ScoresAsync(day)).Single(score => score.UserId == player.Id).Competed);

        player.Client.At(Time(next, 9));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        var today = (await player.Client.GetJsonAsync(Me, Ct)).GetProperty("today").GetProperty("entry");

        Assert.Equal(("NotCompeting", "RecentlyCompeted"), (today.GetProperty("status").GetString(), today.GetProperty("reason").GetString()));
        Assert.Equal("Bu kombin yakın zamanda yarıştı, bugün yarışmıyor.", today.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ACloseThatDiesHalfWayWritesNothingAndTheNextOneDoesItAll()
    {
        var day = NewDay();
        var player = await WornPlayerAsync(day);
        await factory.RunLeagueEntriesAsync(Time(day, 13, 1), [player.Id], Ct);
        var other = await factory.CreatePlayerAsync(day, Ct);
        var otherEntry = await factory.InsertEntryAsync(day, other.Id, cancellationToken: Ct);
        var engine = await factory.InsertEntryAsync(day, null, cancellationToken: Ct);
        using var voter = await factory.CreateUserAsync(Ct);
        await factory.InsertVotesAsync(day, voter.Id, [(otherEntry, engine, LeaguePairChoice.Left, true)], Ct);

        // A score row that should not exist makes the insert of the day's rows fail after the entries were updated.
        await factory.WithDbAsync(async db =>
        {
            db.LeagueDailyScores.Add(new LeagueDailyScore { UserId = other.Id, Day = day });
            return await db.SaveChangesAsync(Ct);
        });

        await Assert.ThrowsAnyAsync<PostgresException>(() => factory.CloseDayAsync(day, Time(day, 21, 1), Ct));

        Assert.Null(await DayRowAsync(day));
        Assert.Empty(await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.Day == day && entry.Strength != null).ToListAsync(Ct)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.LeagueEnginePreferences.CountAsync(row => row.Day == day, Ct)));
        Assert.Single(await ScoresAsync(day));

        await factory.WithDbAsync(db => db.LeagueDailyScores.Where(row => row.Day == day).ExecuteDeleteAsync(Ct));
        var result = await factory.CloseDayAsync(day, Time(day, 22, 1), Ct);

        Assert.Equal((DayCloseOutcome.Closed, 1), (result.Outcome, result.PairCount));
        Assert.Equal(2, (await ScoresAsync(day)).Count);
        Assert.Equal(1, await factory.WithDbAsync(db => db.LeagueEnginePreferences.CountAsync(row => row.Day == day, Ct)));
    }

    /// <summary>
    /// The hourly job closes every unclosed day of its window (the last <c>League:CloseCatchUpDays</c> days whose close has passed), oldest
    /// first, rows or not, and nothing outside it; a second run finds nothing to do.
    /// </summary>
    [Fact]
    public async Task TheHourlyJobCatchesUpOnTheDaysItMissed()
    {
        var first = NewBlock(12);
        var player = await factory.CreatePlayerAsync(first, Ct);
        await factory.InsertEntryAsync(first.AddDays(3), player.Id, cancellationToken: Ct);
        await factory.WithDbAsync(async db =>
        {
            foreach (var day in (DateOnly[])[first, first.AddDays(5)])
            {
                db.LeagueDays.Add(new LeagueDay { Day = day, ClosesAt = Time(day, 21) });
            }

            return await db.SaveChangesAsync(Ct);
        });

        var summary = await factory.RunJobAsync<LeagueCloseJob, LeagueCloseSummary>(job => job.CloseDueAsync(Time(first.AddDays(8), 21, 30), Ct));

        Assert.Equal(Enumerable.Range(2, 7).Select(offset => first.AddDays(offset)), summary.Closed);
        Assert.Equal((false, 0), (summary.LockBusy, summary.Failures));
        Assert.Null((await DayRowAsync(first))!.ClosedAt);
        Assert.Null(await DayRowAsync(first.AddDays(1)));
        Assert.True((await ScoresAsync(first.AddDays(3))).Single(score => score.UserId == player.Id).Competed);

        var again = await factory.RunJobAsync<LeagueCloseJob, LeagueCloseSummary>(job => job.CloseDueAsync(Time(first.AddDays(8), 22), Ct));
        var beforeNine = await factory.RunJobAsync<LeagueCloseJob, LeagueCloseSummary>(job => job.CloseDueAsync(Time(first.AddDays(9), 20, 59, 59), Ct));

        Assert.Empty(again.Closed);
        Assert.Empty(beforeNine.Closed);
        Assert.Null(await DayRowAsync(first.AddDays(9)));
    }
}

/// <summary>One close at a time (<c>pg_try_advisory_xact_lock(0x4B4F4D42, 2)</c>): a close that does not get the lock does nothing.</summary>
[Collection(LeagueCloses.Name)]
public sealed class LeagueCloseLockTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACloseWhileAnotherHoldsTheLockDoesNothingAndSaysSo()
    {
        // The job looks at seven days back from the day: all of them the test's own.
        var first = NewBlock(10).AddDays(7);
        var player = await factory.CreatePlayerAsync(first, Ct);
        await factory.InsertEntryAsync(first, player.Id, cancellationToken: Ct);

        await using (var holder = await factory.OpenConnectionAsync(Ct))
        {
            await using var transaction = await holder.BeginTransactionAsync(Ct);
            await using (var take = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({DayCloser.LockClass}, {DayCloser.LockObject})", holder, transaction))
            {
                await take.ExecuteNonQueryAsync(Ct);
            }

            Assert.Equal(DayCloseOutcome.AlreadyRunning, (await factory.CloseDayAsync(first, Time(first, 21, 5), Ct)).Outcome);
            using (var busy = await factory.InternalCloseAsync(first.Iso(), Time(first, 21, 5), Ct))
            {
                Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
                Assert.Equal("League.CloseInProgress", (await busy.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
            }

            var job = await factory.RunJobAsync<LeagueCloseJob, LeagueCloseSummary>(close => close.CloseDueAsync(Time(first, 21, 5), Ct));
            Assert.True(job.LockBusy);
            Assert.Empty(job.Closed);
            Assert.False(await factory.WithDbAsync(db => db.LeagueDays.AnyAsync(row => row.Day == first && row.ClosedAt != null, Ct)));
            Assert.False(await factory.WithDbAsync(db => db.LeagueDailyScores.AnyAsync(row => row.Day == first, Ct)));

            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(first, Time(first, 21, 6), Ct)).Outcome);
    }
}
