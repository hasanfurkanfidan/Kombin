using System.Net;
using System.Text.Json;
using Kombin.Api.Features.League;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The under-review rule (task L5) and the review decisions (<see cref="LeagueEntryReviewer"/>): reporting a rival never wipes their result; the
/// owner sees "under review" until a reviewer restores the outfit (the stored result appears) or removes it (its vote points become 0, nobody
/// else's rank moves).
/// </summary>
[Collection(LeagueCloses.Name)]
public sealed class LeagueReviewTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ReportedDay(DateOnly Day, TestUser Rival, Guid RivalEntry, TestUser Other, Guid OtherEntry);

    /// <summary>
    /// A day where the rival's outfit, hidden by reports, beat eight engine outfits and the other player's lost to all of them; the rival also
    /// marked an outfit worn (10 usage points). Closed at 21:01.
    /// </summary>
    private async Task<ReportedDay> ClosedDayWithAReportedRivalAsync()
    {
        var day = NewDay();
        var rival = await factory.CreatePlayerAsync(day, Ct);
        var other = await factory.CreatePlayerAsync(day, Ct);
        var rivalEntry = await factory.InsertEntryAsync(day, rival.Id, cancellationToken: Ct);
        var otherEntry = await factory.InsertEntryAsync(day, other.Id, cancellationToken: Ct);
        await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.Id == rivalEntry)
            .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.CountingReports, 3), Ct));
        var engines = new List<Guid>();
        for (var index = 0; index < 8; index++)
        {
            engines.Add(await factory.InsertEntryAsync(day, null, cancellationToken: Ct));
        }

        using var voter = await factory.CreateUserAsync(Ct);
        await factory.InsertVotesAsync(day, voter.Id,
        [
            .. engines.Select(engine => (rivalEntry, engine, Kombin.Api.Persistence.Entities.League.LeaguePairChoice.Left, true)),
            .. engines.Select(engine => (otherEntry, engine, Kombin.Api.Persistence.Entities.League.LeaguePairChoice.Right, true)),
        ], Ct);
        rival.Client.At(Time(day, 13));
        await rival.Client.WearOutfitAsync((await factory.SeedOutfitAsync(rival.Id, Ct)).OutfitId, Ct);

        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(day, Time(day, 21, 1), Ct)).Outcome);

        return new ReportedDay(day, rival, rivalEntry, other, otherEntry);
    }

    private Task<LeagueDailyScore> ScoreAsync(Guid userId, DateOnly day) =>
        factory.WithDbAsync(db => db.LeagueDailyScores.AsNoTracking().SingleAsync(row => row.UserId == userId && row.Day == day, Ct));

    private Task<LeagueReviewResult> ReviewAsync(Guid entryId, LeagueReviewDecision decision) =>
        factory.RunJobAsync<LeagueEntryReviewer, LeagueReviewResult>(reviewer => reviewer.ReviewAsync(entryId, decision, "tests", Ct));

    private static async Task<JsonElement> ResultsOkAsync(HttpClient client, DateOnly day)
    {
        using var response = await client.ResultsAsync(day, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task AReportedRivalIsStillRankedAndItsOwnerSeesUnderReviewUntilARestore()
    {
        var played = await ClosedDayWithAReportedRivalAsync();

        var stored = await ScoreAsync(played.Rival.Id, played.Day);
        Assert.Equal((true, 1, 2, true), (stored.Ranked, stored.Rank, stored.RankedCount, stored.UnderReview));
        Assert.Equal(DailyPointsFor(stored.Percentile!.Value) + 10, stored.Total);

        var held = await ResultsOkAsync(played.Rival.Client, played.Day);
        Assert.Equal("UnderReview", held.GetProperty("status").GetString());
        Assert.Equal("Kombinin inceleniyor. Sonuç inceleme bitince görünecek.", held.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, held.GetProperty("ranked").ValueKind);
        Assert.Equal(JsonValueKind.Null, held.GetProperty("rank").ValueKind);
        Assert.Equal(JsonValueKind.Null, held.GetProperty("points").GetProperty("votes").ValueKind);
        Assert.Equal((10, 10), (held.GetProperty("points").GetProperty("worn").GetInt32(), held.GetProperty("points").GetProperty("total").GetInt32()));

        var other = await ResultsOkAsync(played.Other.Client, played.Day);
        Assert.Equal(("Final", 2), (other.GetProperty("status").GetString(), other.GetProperty("rank").GetInt32()));

        var restored = await ReviewAsync(played.RivalEntry, LeagueReviewDecision.Restore);

        Assert.Equal((LeagueReviewOutcome.Done, LeagueEntryStatus.Competing, true), (restored.Outcome, restored.Status, restored.ResultChanged));
        var visible = await ResultsOkAsync(played.Rival.Client, played.Day);
        Assert.Equal(("Final", 1, stored.VotePoints, stored.Total), (visible.GetProperty("status").GetString(), visible.GetProperty("rank").GetInt32(),
            visible.GetProperty("points").GetProperty("votes").GetInt32(), visible.GetProperty("points").GetProperty("total").GetInt32()));
        Assert.Equal(JsonValueKind.Null, visible.GetProperty("message").ValueKind);
        Assert.Equal(0, await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.Id == played.RivalEntry).Select(entry => entry.CountingReports).SingleAsync(Ct)));
        var audit = await factory.WithDbAsync(db => db.AdminAuditLog.AsNoTracking().SingleAsync(row => row.TargetId == played.RivalEntry.ToString(), Ct));
        Assert.Equal(("tests", "league.entry.restore", LeagueEntryReviewer.TargetType), (audit.Actor, audit.Action, audit.TargetType));
    }

    [Fact]
    public async Task ARemovalAfterTheCloseZeroesThatOutfitsVotePointsAndMovesNoOtherRank()
    {
        var played = await ClosedDayWithAReportedRivalAsync();
        var otherBefore = await ScoreAsync(played.Other.Id, played.Day);

        var removed = await ReviewAsync(played.RivalEntry, LeagueReviewDecision.Remove);

        Assert.Equal((LeagueReviewOutcome.Done, LeagueEntryStatus.Removed, true), (removed.Outcome, removed.Status, removed.ResultChanged));
        var after = await ScoreAsync(played.Rival.Id, played.Day);
        Assert.Equal((0, 10, false, null, false, true), (after.VotePoints, after.Total, after.Ranked, after.Rank, after.UnderReview, after.RemovedByReview));
        Assert.Equivalent(otherBefore, await ScoreAsync(played.Other.Id, played.Day), strict: true);

        var result = await ResultsOkAsync(played.Rival.Client, played.Day);
        Assert.Equal(("Removed", false, 0, 10), (result.GetProperty("status").GetString(), result.GetProperty("ranked").GetBoolean(),
            result.GetProperty("points").GetProperty("votes").GetInt32(), result.GetProperty("points").GetProperty("total").GetInt32()));

        Assert.Equal((LeagueReviewOutcome.Done, false), ((await ReviewAsync(played.RivalEntry, LeagueReviewDecision.Remove)).Outcome, (await ReviewAsync(played.RivalEntry, LeagueReviewDecision.Remove)).ResultChanged));
        Assert.Equal(LeagueReviewOutcome.NotReviewable, (await ReviewAsync(played.RivalEntry, LeagueReviewDecision.Restore)).Outcome);
        Assert.Equal(LeagueReviewOutcome.NotFound, (await ReviewAsync(Guid.NewGuid(), LeagueReviewDecision.Remove)).Outcome);
    }

    [Fact]
    public async Task ARemovalBeforeTheCloseTakesTheOutfitAndItsVotesOutOfTheDay()
    {
        var day = NewDay();
        var player = await factory.CreatePlayerAsync(day, Ct);
        var entryId = await factory.InsertEntryAsync(day, player.Id, cancellationToken: Ct);
        var engine = await factory.InsertEntryAsync(day, null, cancellationToken: Ct);
        using var voter = await factory.CreateUserAsync(Ct);
        await factory.InsertVotesAsync(day, voter.Id, [(entryId, engine, Kombin.Api.Persistence.Entities.League.LeaguePairChoice.Left, true)], Ct);

        Assert.Equal(LeagueReviewOutcome.Done, (await ReviewAsync(entryId, LeagueReviewDecision.Remove)).Outcome);
        var closed = await factory.CloseDayAsync(day, Time(day, 21, 1), Ct);

        Assert.Equal(0, closed.PairCount);
        var score = await ScoreAsync(player.Id, day);
        Assert.Equal((false, 0, false), (score.Competed, score.Comparisons, score.RemovedByReview));
    }

    private static int DailyPointsFor(double percentile) => (int)Math.Round(70m * (decimal)percentile, MidpointRounding.AwayFromZero);
}
