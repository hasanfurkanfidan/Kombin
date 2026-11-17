using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Identity;
using Kombin.Api.Features.League;
using Kombin.Api.Features.Notification.Jobs;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Notification;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Kombin Ligi, PR 4: weeks, groups, the table, idleness, the style score, the review endpoints and the result push (<c>docs/design/league.md</c>,
/// sections 10 to 13). Every test plays a run of days of its own (<see cref="LeagueClientExtensions.NewBlock"/>) and closes its weeks for its own
/// players only (idleness and the next groups would otherwise reach every player of the shared database).
/// </summary>
[Collection(LeagueCloses.Name)]
public sealed class LeagueWeekTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<WeekCloseResult> CloseWeekAsync(DateOnly monday, IReadOnlyCollection<Guid> players) =>
        factory.RunJobAsync<WeekCloser, WeekCloseResult>(closer => closer.CloseWeekAsync(monday, Time(monday.AddDays(6), 21, 5), players, Ct));

    private Task<List<LeagueGroupMember>> MembersAsync(DateOnly monday) =>
        factory.WithDbAsync(db => db.LeagueGroupMembers.AsNoTracking().Where(member => member.WeekStart == monday).ToListAsync(Ct));

    private Task<LeaguePlayer> PlayerAsync(Guid userId) =>
        factory.WithDbAsync(db => db.LeaguePlayers.AsNoTracking().SingleAsync(player => player.UserId == userId, Ct));

    private static async Task<JsonElement> TableAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/league/table", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task AWeekClosesWithBestFiveBandsAndFormsNextWeeksGroupsWithNewAliases()
    {
        var monday = NewMonday();
        var players = new List<TestUser>();
        for (var index = 0; index < 16; index++)
        {
            players.Add(await factory.CreatePlayerAsync(monday, Ct));
        }

        var ids = players.Select(player => player.Id).ToList();
        var week1 = await MembersAsync(monday);
        Assert.Equal(16, week1.Count);
        Assert.Single(week1.Select(member => member.GroupId).Distinct());

        // Player i scores 20 + i on six days and 0 on one: week points = 5 × (20 + i); the last player never plays.
        for (var index = 0; index < 15; index++)
        {
            await factory.InsertWeekScoresAsync(ids[index], monday, 20 + index, 20 + index, 0, 20 + index, 20 + index, 20 + index, 20 + index);
        }

        await factory.CloseDaysDirectlyAsync(monday, 7, Ct);
        Assert.Equal(WeekCloseOutcome.NotReady, (await factory.RunJobAsync<WeekCloser, WeekCloseResult>(closer => closer.CloseWeekAsync(monday, Time(monday.AddDays(6), 20, 59), ids, Ct))).Outcome);

        var result = await CloseWeekAsync(monday, ids);

        Assert.Equal((WeekCloseOutcome.Closed, 16, 5, 0, 1, 15), (result.Outcome, result.Members, result.Promoted, result.Relegated, result.NextGroups, result.NextMembers));
        var closed = (await MembersAsync(monday)).ToDictionary(member => member.UserId);
        for (var index = 0; index < 15; index++)
        {
            Assert.Equal((5 * (20 + index), 15 - index, (short)6), (closed[ids[index]].WeekPoints, closed[ids[index]].FinalRank, closed[ids[index]].CompetedDays));
        }

        Assert.Equal((0, 16, LeagueWeekOutcome.Stayed), (closed[ids[15]].WeekPoints, closed[ids[15]].FinalRank, closed[ids[15]].Outcome));
        Assert.All(ids.Skip(10).Take(5), id => Assert.Equal(LeagueWeekOutcome.Promoted, closed[id].Outcome));
        Assert.All(ids.Take(5), id => Assert.Equal(LeagueWeekOutcome.Stayed, closed[id].Outcome)); // the bottom five are in Bronze: they stay
        Assert.All(ids.Skip(10).Take(5), id => Assert.Equal(LeagueTier.Silver, PlayerAsync(id).Result.Tier));
        Assert.Equal(((short)1, LeagueTier.Bronze), ((await PlayerAsync(ids[15])).IdleWeeks, (await PlayerAsync(ids[15])).Tier));

        // Next week: one group of the fifteen who played (Bronze 10 < 15 merges with Silver 5), each keeping their label, all with a new alias.
        var week2 = await MembersAsync(monday.AddDays(7));
        Assert.Equal(15, week2.Count);
        Assert.DoesNotContain(week2, member => member.UserId == ids[15]);
        var group = Assert.Single(await factory.WithDbAsync(db => db.LeagueGroups.AsNoTracking().Where(row => row.WeekStart == monday.AddDays(7)).ToListAsync(Ct)));
        Assert.Equal([LeagueTier.Bronze, LeagueTier.Silver], group.Tiers);
        Assert.All(week2, member => Assert.NotEqual(closed[member.UserId].Alias, member.Alias));
        Assert.Equal(15, week2.Select(member => member.Alias).Distinct().Count());
        Assert.Equal(5, week2.Count(member => member.Tier == LeagueTier.Silver));
        Assert.NotNull((await factory.WithDbAsync(db => db.LeagueWeeks.AsNoTracking().SingleAsync(row => row.WeekStart == monday.AddDays(7), Ct))).GroupsFormedAt);

        // Closing again changes nothing.
        Assert.Equal(WeekCloseOutcome.AlreadyClosed, (await CloseWeekAsync(monday, ids)).Outcome);
        Assert.Equivalent(closed.Values.OrderBy(member => member.UserId).ToList(), (await MembersAsync(monday)).OrderBy(member => member.UserId).ToList(), strict: true);

        // Sunday evening: last week's final table; Monday: the new group.
        var bottom = players[0].Client.At(Time(monday.AddDays(6), 22));
        var final = await TableAsync(bottom);
        Assert.Equal((monday.Iso(), 16, "Bronze"), (final.GetProperty("weekStart").GetString(), final.GetProperty("size").GetInt32(), final.GetProperty("tier").GetString()));
        Assert.Equal(Enumerable.Range(1, 16), final.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("rank").GetInt32()));
        var me = Assert.Single(final.GetProperty("rows").EnumerateArray(), row => row.GetProperty("isMe").GetBoolean());
        Assert.Equal((15, 100, closed[ids[0]].Alias), (me.GetProperty("rank").GetInt32(), me.GetProperty("points").GetInt32(), me.GetProperty("alias").GetString()));
        Assert.All(final.GetProperty("rows").EnumerateArray(), row => Assert.Equal(["alias", "isMe", "points", "rank"], row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)));
        Assert.Equal((true, 5, 5), (final.GetProperty("todayIsFinal").GetBoolean(), final.GetProperty("promoteCount").GetInt32(), final.GetProperty("relegateCount").GetInt32()));

        var next = await TableAsync(bottom.At(Time(monday.AddDays(7), 12)));
        Assert.Equal((monday.AddDays(7).Iso(), 15, "Bronze", "Silver"), (next.GetProperty("weekStart").GetString(), next.GetProperty("size").GetInt32(),
            next.GetProperty("tier").GetString(), next.GetProperty("tierUp").GetString()));
        Assert.Equal(JsonValueKind.Null, next.GetProperty("tierDown").ValueKind);
        var meWeek = (await bottom.GetJsonAsync(Me, Ct)).GetProperty("week");
        Assert.Equal(15, meWeek.GetProperty("groupSize").GetInt32());
    }

    [Fact]
    public async Task TwoIdleWeeksInARowCostOneTierGroupOrNot()
    {
        var monday = NewMonday();
        var player = await factory.CreatePlayerAsync(monday, Ct);
        await factory.WithDbAsync(db => db.LeaguePlayers.Where(row => row.UserId == player.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Tier, LeagueTier.Gold), Ct));
        await factory.CloseDaysDirectlyAsync(monday, 14, Ct);

        await CloseWeekAsync(monday, [player.Id]);
        Assert.Equal(((short)1, LeagueTier.Gold), ((await PlayerAsync(player.Id)).IdleWeeks, (await PlayerAsync(player.Id)).Tier));

        var second = await CloseWeekAsync(monday.AddDays(7), [player.Id]);

        Assert.Equal(1, second.IdleRelegated);
        Assert.Equal(((short)0, LeagueTier.Silver), ((await PlayerAsync(player.Id)).IdleWeeks, (await PlayerAsync(player.Id)).Tier));
    }

    [Fact]
    public async Task AMidWeekJoinerGoesToTheEmptiestGroupOfTheirTierOrANewOne()
    {
        var monday = NewMonday();
        var early = new List<TestUser>();
        for (var index = 0; index < 3; index++)
        {
            early.Add(await factory.CreatePlayerAsync(monday, Ct));
        }

        var bronzeGroup = Assert.Single((await MembersAsync(monday)).Select(member => member.GroupId).Distinct());

        var wednesday = monday.AddDays(2);
        var joiner = await factory.CreatePlayerAsync(wednesday, Ct);
        using var gold = await factory.CreateUserAsync(Ct);
        await factory.WithDbAsync(async db =>
        {
            db.LeaguePlayers.Add(new LeaguePlayer { UserId = gold.Id, Tier = LeagueTier.Gold, JoinedAt = Time(wednesday, 9), ConsentVersion = LeagueConsent.Current.Version });
            return await db.SaveChangesAsync(Ct);
        });
        await TableAsync(gold.Client.At(Time(wednesday, 12)));

        var members = (await MembersAsync(monday)).ToDictionary(member => member.UserId);
        Assert.Equal(bronzeGroup, members[joiner.Id].GroupId);
        Assert.NotEqual(bronzeGroup, members[gold.Id].GroupId);
        Assert.Equal(LeagueTier.Gold, members[gold.Id].Tier);
        Assert.Equal([LeagueTier.Gold], await factory.WithDbAsync(db => db.LeagueGroups.Where(group => group.Id == members[gold.Id].GroupId).Select(group => group.Tiers).SingleAsync(Ct)));
        Assert.Equal(4, members.Values.Count(member => member.GroupId == bronzeGroup));
    }

    [Fact]
    public async Task APlayerWhoLeftAndCameBackKeepsOneHistory()
    {
        var monday = NewMonday();
        var player = await factory.CreatePlayerAsync(monday, Ct);
        await factory.InsertWeekScoresAsync(player.Id, monday, 40);
        await factory.CloseDaysDirectlyAsync(monday, 1, Ct);
        Assert.Single(await MembersAsync(monday));

        player.Client.At(Time(monday.AddDays(1), 10));
        using (var left = await player.Client.LeaveLeagueAsync(Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }

        Assert.Empty(await MembersAsync(monday));
        using (var table = await player.Client.GetAsync("/api/v1/league/table", Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, table.StatusCode);
        }

        using (var results = await player.Client.ResultsAsync(monday, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, results.StatusCode);
        }

        await player.Client.JoinLeagueOkAsync(Ct);

        Assert.Single(await MembersAsync(monday));
        Assert.Equal(1, await factory.WithDbAsync(db => db.LeaguePlayers.CountAsync(row => row.UserId == player.Id, Ct)));
        Assert.Equal(3, await factory.WithDbAsync(db => db.LeagueConsents.CountAsync(row => row.UserId == player.Id, Ct)));
        var row = Assert.Single((await TableAsync(player.Client)).GetProperty("rows").EnumerateArray());
        Assert.Equal((40, true), (row.GetProperty("points").GetInt32(), row.GetProperty("isMe").GetBoolean()));
    }

    [Fact]
    public async Task ADeletedAccountVanishesFromGroupsWithoutBreakingTheOthersRanks()
    {
        var monday = NewMonday();
        var players = new List<TestUser>();
        for (var index = 0; index < 3; index++)
        {
            players.Add(await factory.CreatePlayerAsync(monday, Ct));
            await factory.InsertWeekScoresAsync(players[^1].Id, monday, 30 + (10 * index));
        }

        await factory.CloseDaysDirectlyAsync(monday, 7, Ct);
        await CloseWeekAsync(monday, [.. players.Select(player => player.Id)]);

        Assert.Equal(PurgeStatus.Purged, (await factory.RunJobAsync<AccountPurger, PurgeResult>(purger => purger.PurgeAsync(players[1].Id, Ct))).Status);

        var closedWeek = await TableAsync(players[0].Client.At(Time(monday.AddDays(6), 22)));
        Assert.Equal([1, 2], closedWeek.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("rank").GetInt32()));
        Assert.Equal([50, 30], closedWeek.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("points").GetInt32()));
        var nextWeek = await TableAsync(players[2].Client.At(Time(monday.AddDays(8), 12)));
        Assert.Equal([1, 2], nextWeek.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("rank").GetInt32()));
        Assert.DoesNotContain(await MembersAsync(monday), member => member.UserId == players[1].Id);
    }

    /// <summary>A week close that fails at its last step (a trigger refuses this player's style score row) leaves the week exactly as it was.</summary>
    [Fact]
    public async Task AWeekCloseThatDiesHalfWayWritesNothing()
    {
        var monday = NewMonday();
        var players = new List<TestUser>();
        for (var index = 0; index < 3; index++)
        {
            players.Add(await factory.CreatePlayerAsync(monday, Ct));
            await factory.InsertWeekScoresAsync(players[^1].Id, monday, 50 + index);
        }

        var ids = players.Select(player => player.Id).ToList();
        await factory.CloseDaysDirectlyAsync(monday, 7, Ct);
        var trigger = $"league_test_fail_{Guid.NewGuid():N}";
        await using (var connection = await factory.OpenConnectionAsync(Ct))
        {
            await using var create = new NpgsqlCommand(
                $"""
                CREATE FUNCTION league.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW.user_id = '{ids[2]}' THEN RAISE EXCEPTION 'simulated failure'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER {trigger} BEFORE INSERT ON league.style_scores FOR EACH ROW EXECUTE FUNCTION league.{trigger}();
                """, connection);
            await create.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => CloseWeekAsync(monday, ids));
        }
        finally
        {
            await using var connection = await factory.OpenConnectionAsync(Ct);
            await using var drop = new NpgsqlCommand($"DROP TRIGGER {trigger} ON league.style_scores; DROP FUNCTION league.{trigger}();", connection);
            await drop.ExecuteNonQueryAsync(Ct);
        }

        Assert.Null((await factory.WithDbAsync(db => db.LeagueWeeks.AsNoTracking().SingleAsync(row => row.WeekStart == monday, Ct))).ClosedAt);
        Assert.All(await MembersAsync(monday), member => Assert.Null(member.FinalRank));
        Assert.Empty(await MembersAsync(monday.AddDays(7)));
        Assert.All(ids, id => Assert.Equal((short)0, PlayerAsync(id).Result.IdleWeeks));
        Assert.False(await factory.WithDbAsync(db => db.LeagueStyleScores.AnyAsync(row => ids.Contains(row.UserId), Ct)));

        var retried = await CloseWeekAsync(monday, ids);

        Assert.Equal((WeekCloseOutcome.Closed, 3, 3), (retried.Outcome, retried.NextMembers, retried.StyleScores));
    }

    [Fact]
    public async Task TheLiveTableCountsTheClosedDaysAndTodaysUsageSoFar()
    {
        var monday = NewMonday();
        var me = await factory.CreatePlayerAsync(monday, Ct);
        var other = await factory.CreatePlayerAsync(monday, Ct);
        await factory.InsertWeekScoresAsync(me.Id, monday, 30, 40);
        await factory.InsertWeekScoresAsync(other.Id, monday, 80);
        await factory.CloseDaysDirectlyAsync(monday, 2, Ct);

        var wednesday = monday.AddDays(2);
        me.Client.At(Time(wednesday, 9));
        await me.Client.WearOutfitAsync((await factory.SeedOutfitAsync(me.Id, Ct)).OutfitId, Ct);
        var table = await TableAsync(me.Client.At(Time(wednesday, 12)));

        Assert.Equal((10, false), (table.GetProperty("todayPoints").GetInt32(), table.GetProperty("todayIsFinal").GetBoolean()));
        Assert.Equal([80, 70], table.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("points").GetInt32()));
        Assert.Equal((0, 0), (table.GetProperty("promoteCount").GetInt32(), table.GetProperty("relegateCount").GetInt32()));
        var week = (await me.Client.GetJsonAsync(Me, Ct)).GetProperty("week");
        Assert.Equal((2, 2, 70), (week.GetProperty("groupRank").GetInt32(), week.GetProperty("groupSize").GetInt32(), week.GetProperty("points").GetInt32()));
        Assert.Equal(JsonValueKind.Null, week.GetProperty("pointsToPromotion").ValueKind);
    }

    /// <summary>
    /// The result push: once per player and day after the close, the owner's text, the preference and the quiet hours respected, a suppressed row
    /// without a device, nothing for a player who left after the close, and a result under review only once a reviewer restored it.
    /// </summary>
    [Fact]
    public async Task TheResultPushGoesOutOncePerPlayerAfterTheClose()
    {
        var day = NewDay();
        var players = new Dictionary<string, TestUser>();
        foreach (var name in new[] { "on", "off", "nodevice", "quiet", "left", "review" })
        {
            players[name] = await factory.CreatePlayerAsync(day, Ct);
            var entryId = await factory.InsertEntryAsync(day, players[name].Id, cancellationToken: Ct);
            if (name == "review")
            {
                await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.Id == entryId).ExecuteUpdateAsync(set => set.SetProperty(entry => entry.CountingReports, 3), Ct));
            }

            if (name != "nodevice")
            {
                await players[name].Client.RegisterDeviceAsync(cancellationToken: Ct);
            }
        }

        (await players["off"].Client.PutPreferencesAsync(new { leagueResults = false }, Ct)).Dispose();
        (await players["quiet"].Client.PutPreferencesAsync(new { quietHoursStart = "20:00", quietHoursEnd = "08:00" }, Ct)).Dispose();
        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(day, Time(day, 21, 1), Ct)).Outcome);
        using (var left = await players["left"].Client.At(Time(day, 21, 10)).LeaveLeagueAsync(Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }

        var ids = players.Values.Select(player => player.Id).ToList();
        Task<LeagueResultsSummary> SendAsync(DateTimeOffset now) => factory.RunJobAsync<LeagueResultsJob, LeagueResultsSummary>(job => job.SendAsync(now, ids, Ct));
        Task<List<Notification>> PushesAsync() => factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(row => ids.Contains(row.UserId) && row.Kind == NotificationKind.LeagueResult).ToListAsync(Ct));

        var first = await SendAsync(Time(day, 21, 15));

        Assert.Equal((5, 3, 2), (first.Handled, first.Created, first.Skipped));
        var pushes = (await PushesAsync()).ToDictionary(row => row.UserId);
        Assert.Equal([players["on"].Id, players["nodevice"].Id, players["quiet"].Id], pushes.Keys.Order());
        var on = pushes[players["on"].Id];
        Assert.Equal(("Kombin Ligi sonuçları", "Bugün yeterli oy toplanamadı. Puanın 0.", NotificationStatus.Pending), (on.Title, on.Body, on.Status));
        Assert.Contains($"kombin://league/results?day={day.Iso()}", on.Data, StringComparison.Ordinal);
        Assert.Equal($"league:{day.Iso()}", on.DedupeKey);
        Assert.Equal((NotificationStatus.Suppressed, Notification.NoDevice), (pushes[players["nodevice"].Id].Status, pushes[players["nodevice"].Id].FailureReason));
        Assert.Equal(LeagueDays.ClosesAt(day.AddDays(1), new TimeOnly(8, 0)), pushes[players["quiet"].Id].ScheduledFor);

        Assert.Equal(0, (await SendAsync(Time(day, 21, 20))).Created);

        var underReview = await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.UserId == players["review"].Id && entry.Day == day).Select(entry => entry.Id).SingleAsync(Ct));
        await factory.RunJobAsync<LeagueEntryReviewer, LeagueReviewResult>(reviewer => reviewer.ReviewAsync(underReview, LeagueReviewDecision.Restore, "tests", Ct));
        Assert.Equal(1, (await SendAsync(Time(day, 22))).Created);
        Assert.Equal(4, (await PushesAsync()).Count);
        Assert.Equal(0, (await SendAsync(Time(day, 23))).Created);
    }

    [Fact]
    public async Task TheReviewQueueListsReportedOutfitsAndTheDecisionIsAudited()
    {
        var day = NewDay();
        var owner = await factory.CreatePlayerAsync(day, Ct);
        var entryId = await factory.InsertEntryAsync(day, owner.Id, cancellationToken: Ct);
        var other = await factory.InsertEntryAsync(day, (await factory.CreatePlayerAsync(day, Ct)).Id, cancellationToken: Ct);
        using var reporter = await factory.CreateUserAsync(Ct);
        await factory.WithDbAsync(async db =>
        {
            db.LeagueReports.Add(new LeagueReport { EntryId = entryId, ReporterId = reporter.Id, Reason = LeagueReportReason.NotClothing, Counts = true, CreatedAt = TestClock.UtcNow.AddMinutes(-5) });
            return await db.SaveChangesAsync(Ct);
        });

        var queue = await InternalAsync<JsonElement>(HttpMethod.Get, "/api/v1/internal/league/review-queue", null, HttpStatusCode.OK);
        var item = Assert.Single(queue.EnumerateArray(), row => row.GetProperty("entryId").GetGuid() == entryId);
        Assert.Equal((1, "Competing", 2), (item.GetProperty("reports").GetInt32(), item.GetProperty("status").GetString(), item.GetProperty("pieces").GetArrayLength()));
        Assert.DoesNotContain(queue.EnumerateArray(), row => row.GetProperty("entryId").GetGuid() == other);

        await InternalAsync<JsonElement>(HttpMethod.Post, $"/api/v1/internal/league/entries/{entryId}/review", new { decision = "Keep" }, HttpStatusCode.BadRequest);
        await InternalAsync<JsonElement>(HttpMethod.Post, $"/api/v1/internal/league/entries/{Guid.NewGuid()}/review", new { decision = "Remove" }, HttpStatusCode.NotFound);
        var restored = await InternalAsync<JsonElement>(HttpMethod.Post, $"/api/v1/internal/league/entries/{entryId}/review", new { decision = "restore" }, HttpStatusCode.OK);
        Assert.Equal(("Competing", false), (restored.GetProperty("status").GetString(), restored.GetProperty("resultChanged").GetBoolean()));

        var after = await InternalAsync<JsonElement>(HttpMethod.Get, "/api/v1/internal/league/review-queue", null, HttpStatusCode.OK);
        Assert.DoesNotContain(after.EnumerateArray(), row => row.GetProperty("entryId").GetGuid() == entryId);
        var audit = await factory.WithDbAsync(db => db.AdminAuditLog.AsNoTracking().SingleAsync(row => row.TargetId == entryId.ToString(), Ct));
        Assert.Equal(("internal-key", "league.entry.restore"), (audit.Actor, audit.Action));

        await InternalAsync<JsonElement>(HttpMethod.Post, $"/api/v1/internal/league/entries/{entryId}/review", new { decision = "Remove" }, HttpStatusCode.OK);
        var conflict = await InternalAsync<JsonElement>(HttpMethod.Post, $"/api/v1/internal/league/entries/{entryId}/review", new { decision = "Restore" }, HttpStatusCode.Conflict);
        Assert.Equal("League.NotReviewable", conflict.GetProperty("errorCode").GetString());
    }

    private async Task<T> InternalAsync<T>(HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Internal-Key", ApiFactory.InternalApiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{method} {path}: {(int)response.StatusCode} {text}");

        return JsonSerializer.Deserialize<T>(text)!;
    }
}
