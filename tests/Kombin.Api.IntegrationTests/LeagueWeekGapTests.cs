using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.League;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The gap between Sunday's 21:00 and the week close (task B9, item 1). While the week that just ended has not been closed, nobody is placed into the
/// next week (the close forms the groups, at the tiers the week decided); <c>GET /league/me</c> and <c>GET /league/table</c> keep showing the week
/// that ended, with <c>state: "Ending"</c>, and never answer an error or a group of one for a player who has a table.
/// </summary>
[Collection(LeagueCloses.Name)]
public sealed class LeagueWeekGapTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<WeekCloseResult> CloseWeekAtAsync(DateOnly monday, DateTimeOffset now, IReadOnlyCollection<Guid> players) =>
        factory.RunJobAsync<WeekCloser, WeekCloseResult>(closer => closer.CloseWeekAsync(monday, now, players, Ct));

    private Task<List<LeagueGroupMember>> MembersAsync(DateOnly monday) =>
        factory.WithDbAsync(db => db.LeagueGroupMembers.AsNoTracking().Where(member => member.WeekStart == monday).ToListAsync(Ct));

    /// <summary>Three players in one Bronze group; the third wins the week, so the close promotes them to Silver. All seven days are closed.</summary>
    private async Task<(DateOnly Monday, List<TestUser> Players)> PlayedWeekAsync()
    {
        var monday = NewMonday();
        var players = new List<TestUser>();
        for (var index = 0; index < 3; index++)
        {
            players.Add(await factory.CreatePlayerAsync(monday, Ct));
            await factory.InsertWeekScoresAsync(players[^1].Id, monday, 30 + (10 * index));
        }

        await factory.CloseDaysDirectlyAsync(monday, 7, Ct);

        return (monday, players);
    }

    private static async Task<JsonElement> TableOkAsync(HttpClient client, DateTimeOffset now)
    {
        using var response = await client.At(now).GetAsync("/api/v1/league/table", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertLastWeeksTableAsync(TestUser player, DateOnly monday, DateTimeOffset now)
    {
        var table = await TableOkAsync(player.Client, now);
        Assert.Equal((monday.Iso(), "Ending", 3, "Bronze"), (table.GetProperty("weekStart").GetString(), table.GetProperty("state").GetString(), table.GetProperty("size").GetInt32(), table.GetProperty("tier").GetString()));
        Assert.Equal(3, table.GetProperty("rows").GetArrayLength());

        var week = (await player.Client.At(now).GetJsonAsync(Me, Ct)).GetProperty("week");
        Assert.Equal(JsonValueKind.Object, week.ValueKind);
        Assert.Equal(("Ending", 3), (week.GetProperty("state").GetString(), week.GetProperty("groupSize").GetInt32()));
    }

    /// <summary>The next week's groups are the close's: the winner is in Silver, the others in Bronze, one group, nobody was there before.</summary>
    private async Task AssertTheNewTiersAsync(DateOnly monday, List<TestUser> players)
    {
        var next = (await MembersAsync(monday.AddDays(7))).ToDictionary(member => member.UserId);
        Assert.Equal(3, next.Count);
        Assert.Equal((LeagueTier.Bronze, LeagueTier.Bronze, LeagueTier.Silver), (next[players[0].Id].Tier, next[players[1].Id].Tier, next[players[2].Id].Tier));
        Assert.Single(next.Values.Select(member => member.GroupId).Distinct());

        var table = await TableOkAsync(players[2].Client, Time(monday.AddDays(7), 12));
        Assert.Equal((monday.AddDays(7).Iso(), "Open", 3, "Silver"), (table.GetProperty("weekStart").GetString(), table.GetProperty("state").GetString(), table.GetProperty("size").GetInt32(), table.GetProperty("tier").GetString()));
    }

    [Fact]
    public async Task AfterSundaysCloseAndBeforeTheWeekCloseNobodyIsPlacedIntoNextWeek()
    {
        var (monday, players) = await PlayedWeekAsync();
        var gap = Time(monday.AddDays(6), 21, 0, 1);

        foreach (var player in players)
        {
            await AssertLastWeeksTableAsync(player, monday, gap);
        }

        Assert.Empty(await MembersAsync(monday.AddDays(7)));

        // Someone who joins inside the gap has no table to show: the same "no group yet" as ever, not a group of one, and no placement either.
        using var joiner = await factory.CreateUserAsync(Ct);
        await joiner.Client.At(Time(monday.AddDays(6), 21, 0, 30)).JoinLeagueOkAsync(Ct);
        using (var none = await joiner.Client.GetAsync("/api/v1/league/table", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        }

        Assert.Equal(JsonValueKind.Null, (await joiner.Client.GetJsonAsync(Me, Ct)).GetProperty("week").ValueKind);
        Assert.Empty(await MembersAsync(monday.AddDays(7)));

        var closed = await CloseWeekAtAsync(monday, Time(monday.AddDays(6), 21, 5), [.. players.Select(player => player.Id)]);

        Assert.Equal((WeekCloseOutcome.Closed, 3), (closed.Outcome, closed.NextMembers));
        await AssertTheNewTiersAsync(monday, players);

        // The joiner is placed once the close has run, with the new week's rule for a late arrival.
        var placed = await TableOkAsync(joiner.Client, Time(monday.AddDays(7), 8));
        Assert.Equal((monday.AddDays(7).Iso(), "Open", "Bronze"), (placed.GetProperty("weekStart").GetString(), placed.GetProperty("state").GetString(), placed.GetProperty("tier").GetString()));
    }

    /// <summary>A close that is still running (it waits for the week's row) never lets a request place anybody under the old tiers.</summary>
    [Fact]
    public async Task ARequestDuringASlowCloseDoesNotPlaceAnyone()
    {
        var (monday, players) = await PlayedWeekAsync();
        var ids = players.Select(player => player.Id).ToList();
        var gap = Time(monday.AddDays(6), 21, 0, 30);

        await using var holder = await factory.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM league.weeks WHERE week_start = @week FOR UPDATE", holder, transaction))
        {
            hold.Parameters.AddWithValue("week", monday);
            await hold.ExecuteScalarAsync(Ct);
        }

        var closing = CloseWeekAtAsync(monday, Time(monday.AddDays(6), 21, 5), ids);
        await factory.WaitForLockWaitAsync("league.weeks", Ct);

        foreach (var player in players)
        {
            await AssertLastWeeksTableAsync(player, monday, gap);
        }

        Assert.Empty(await MembersAsync(monday.AddDays(7)));

        await transaction.CommitAsync(Ct);
        Assert.Equal(WeekCloseOutcome.Closed, (await closing).Outcome);
        await AssertTheNewTiersAsync(monday, players);
    }

    /// <summary>A close that failed on Sunday evening and is retried on Monday: the players see last week's table in between, and are placed by the retry.</summary>
    [Fact]
    public async Task AFailedCloseRetriedOnMondayPlacesEveryoneAtTheNewTiers()
    {
        var (monday, players) = await PlayedWeekAsync();
        var ids = players.Select(player => player.Id).ToList();
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
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => CloseWeekAtAsync(monday, Time(monday.AddDays(6), 21, 5), ids));
        }
        finally
        {
            await using var connection = await factory.OpenConnectionAsync(Ct);
            await using var drop = new NpgsqlCommand($"DROP TRIGGER {trigger} ON league.style_scores; DROP FUNCTION league.{trigger}();", connection);
            await drop.ExecuteNonQueryAsync(Ct);
        }

        // Monday morning of the new week, the close has not succeeded yet.
        var monday8 = Time(monday.AddDays(7), 8);
        foreach (var player in players)
        {
            await AssertLastWeeksTableAsync(player, monday, monday8);
        }

        Assert.Empty(await MembersAsync(monday.AddDays(7)));

        var retried = await CloseWeekAtAsync(monday, Time(monday.AddDays(7), 9), ids);

        Assert.Equal((WeekCloseOutcome.Closed, 3), (retried.Outcome, retried.NextMembers));
        await AssertTheNewTiersAsync(monday, players);
    }
}
