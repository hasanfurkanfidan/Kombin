using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.League;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Kombin.Api.IntegrationTests;

/// <summary>Kombin Ligi, PR 1: the switch, the consent, joining and leaving (<c>docs/design/league.md</c>, sections 4 and 12).</summary>
public sealed class LeagueTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<List<LeagueConsentRecord>> ConsentsAsync(Guid userId) =>
        factory.WithDbAsync(db => db.LeagueConsents.AsNoTracking().Where(row => row.UserId == userId).OrderBy(row => row.Id).ToListAsync(Ct));

    private Task<LeaguePlayer?> PlayerAsync(Guid userId) =>
        factory.WithDbAsync(db => db.LeaguePlayers.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId, Ct));

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString();

    private static async Task SetBirthYearAsync(HttpClient client, int birthYear)
    {
        using var response = await client.PutAsJsonAsync("/api/v1/me/profile", new { birthYear }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BeforeJoiningTheStateAsksForTheCurrentConsent()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var me = await user.Client.GetJsonAsync(LeagueClientExtensions.Me, Ct);

        Assert.False(me.GetProperty("joined").GetBoolean());
        Assert.True(me.GetProperty("consentRequired").GetBoolean());
        Assert.Equal(LeagueConsent.Current.Version, me.GetProperty("consent").GetProperty("version").GetString());
        Assert.Equal(LeagueConsent.Current.Text, me.GetProperty("consent").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("tier").ValueKind);
        Assert.Null(await PlayerAsync(user.Id));
    }

    [Fact]
    public async Task JoiningRecordsTheExactTextShownAndStartsInBronze()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var joined = await user.Client.JoinLeagueOkAsync(Ct);

        Assert.True(joined.GetProperty("joined").GetBoolean());
        Assert.False(joined.GetProperty("consentRequired").GetBoolean());
        Assert.Equal("Bronze", joined.GetProperty("tier").GetString());
        var consent = Assert.Single(await ConsentsAsync(user.Id));
        Assert.Equal((LeagueConsentAction.Join, LeagueConsent.Current.Version, LeagueConsent.Current.Text), (consent.Action, consent.Version, consent.Text));
        var player = await PlayerAsync(user.Id);
        Assert.Equal((LeagueTier.Bronze, LeagueConsent.Current.Version, 0), (player!.Tier, player.ConsentVersion, player.CountedVotes));
        Assert.Equal(joined.GetProperty("joinedAt").GetDateTimeOffset(), player.JoinedAt, TimeSpan.FromMilliseconds(1));
        Assert.True((await user.Client.GetJsonAsync(LeagueClientExtensions.Me, Ct)).GetProperty("joined").GetBoolean());
    }

    [Fact]
    public async Task JoiningTwiceChangesNothing()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var first = await user.Client.JoinLeagueOkAsync(Ct);
        var second = await user.Client.JoinLeagueOkAsync(Ct);

        Assert.Equal(first.GetProperty("joinedAt").GetDateTimeOffset(), second.GetProperty("joinedAt").GetDateTimeOffset());
        Assert.Single(await ConsentsAsync(user.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2020-01-v0")]
    [InlineData("2026-11-v1")]
    public async Task OnlyTheCurrentConsentVersionCanBeAccepted(string? version)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.JoinLeagueAsync(version, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("League.ConsentVersionInvalid", await ErrorCodeAsync(response));
        Assert.Null(await PlayerAsync(user.Id));
        Assert.Empty(await ConsentsAsync(user.Id));
    }

    // Strict: 17 by the year, and 18 by the year (who may still be 17 until the birthday), both wait.
    [Theory]
    [InlineData(17)]
    [InlineData(18)]
    public async Task ABirthYearThatMayStillBeUnderEighteenCannotJoin(int yearsAgo)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var day = LeagueClientExtensions.NewDay();
        user.Client.At(LeagueClientExtensions.Time(day, 12));
        var thisYear = day.Year;
        await SetBirthYearAsync(user.Client, thisYear - yearsAgo);

        using var response = await user.Client.JoinLeagueAsync(LeagueConsent.Current.Version, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("League.AgeRestricted", await ErrorCodeAsync(response));
        Assert.Null(await PlayerAsync(user.Id));
        Assert.Empty(await ConsentsAsync(user.Id));
    }

    [Fact]
    public async Task CertainlyEighteenOrAnUnknownYearMayJoin()
    {
        using var adult = await factory.CreateUserAsync(Ct);
        using var unknown = await factory.CreateUserAsync(Ct);
        var day = LeagueClientExtensions.NewDay();
        adult.Client.At(LeagueClientExtensions.Time(day, 12));
        unknown.Client.At(LeagueClientExtensions.Time(day, 12));
        await SetBirthYearAsync(adult.Client, day.Year - 19);

        Assert.True((await adult.Client.JoinLeagueOkAsync(Ct)).GetProperty("joined").GetBoolean());
        Assert.True((await unknown.Client.JoinLeagueOkAsync(Ct)).GetProperty("joined").GetBoolean());
    }

    /// <summary>A competing entry of <paramref name="day"/> for a joined player, who is made Gold.</summary>
    private Task<Guid> InsertCompetingEntryAsync(Guid userId, DateOnly day) => factory.WithDbAsync(async db =>
    {
        var entry = new LeagueEntry
        {
            Day = day,
            UserId = userId,
            Kind = LeagueEntryKind.Human,
            Audience = Audience.Unisex,
            PieceIds = [Guid.NewGuid(), Guid.NewGuid()],
            PieceSetHash = new string('b', LeagueEntry.PieceSetHashLength),
            Status = LeagueEntryStatus.Competing,
        };
        db.LeagueEntries.Add(entry);
        await db.LeaguePlayers.Where(row => row.UserId == userId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Tier, LeagueTier.Gold), Ct);
        await db.SaveChangesAsync(Ct);

        return entry.Id;
    });

    private Task<LeagueEntryStatus> EntryStatusAsync(Guid entryId) =>
        factory.WithDbAsync(db => db.LeagueEntries.Where(row => row.Id == entryId).Select(row => row.Status).SingleAsync(Ct));

    // Leaving at noon, before the day's close (21:00 Istanbul): today's outfit leaves the pool.
    [Fact]
    public async Task LeavingRecordsItWithdrawsTodaysOutfitAndAJoinLaterKeepsTheTier()
    {
        var day = LeagueClientExtensions.NewDay();
        using var user = await factory.CreatePlayerAsync(day, Ct);
        var entryId = await InsertCompetingEntryAsync(user.Id, day);

        using var left = await user.Client.LeaveLeagueAsync(Ct);

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        var me = await user.Client.GetJsonAsync(LeagueClientExtensions.Me, Ct);
        Assert.False(me.GetProperty("joined").GetBoolean());
        Assert.True(me.GetProperty("consentRequired").GetBoolean());
        Assert.Equal("Gold", me.GetProperty("tier").GetString());
        Assert.Equal(LeagueEntryStatus.Withdrawn, await EntryStatusAsync(entryId));
        Assert.Equal([LeagueConsentAction.Join, LeagueConsentAction.Leave], (await ConsentsAsync(user.Id)).Select(row => row.Action));
        Assert.NotNull((await PlayerAsync(user.Id))!.LeftAt);

        var back = await user.Client.JoinLeagueOkAsync(Ct);

        Assert.True(back.GetProperty("joined").GetBoolean());
        Assert.Equal("Gold", back.GetProperty("tier").GetString());
        Assert.Null((await PlayerAsync(user.Id))!.LeftAt);
        Assert.Equal([LeagueConsentAction.Join, LeagueConsentAction.Leave, LeagueConsentAction.Join], (await ConsentsAsync(user.Id)).Select(row => row.Action));
    }

    // Leaving at the close (21:00:00 Istanbul is already past the day) or later: the day is over, its entry stays as it is for the close to
    // score; the leave itself is recorded all the same.
    [Theory]
    [InlineData(21, 0)]
    [InlineData(23, 30)]
    public async Task LeavingAfterTheDaysCloseLeavesThatDaysOutfitAsItIs(int hour, int minute)
    {
        var day = LeagueClientExtensions.NewDay();
        using var user = await factory.CreatePlayerAsync(day, Ct);
        var entryId = await InsertCompetingEntryAsync(user.Id, day);
        user.Client.At(LeagueClientExtensions.Time(day, hour, minute));

        using var left = await user.Client.LeaveLeagueAsync(Ct);

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        Assert.Equal(LeagueEntryStatus.Competing, await EntryStatusAsync(entryId));
        Assert.False((await user.Client.GetJsonAsync(LeagueClientExtensions.Me, Ct)).GetProperty("joined").GetBoolean());
        Assert.Equal([LeagueConsentAction.Join, LeagueConsentAction.Leave], (await ConsentsAsync(user.Id)).Select(row => row.Action));
        Assert.Equal(LeagueClientExtensions.Time(day, hour, minute), (await PlayerAsync(user.Id))!.LeftAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// A player who joined under version 1 is asked again (version 2 says the pieces can be recognised): <c>consentRequired</c> is true until they
    /// accept version 2, which writes a new consent row with the exact new text and keeps their tier.
    /// </summary>
    [Fact]
    public async Task APlayerUnderVersionOneIsAskedAgainAndAcceptsVersionTwo()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.JoinLeagueOkAsync(Ct);
        await factory.WithDbAsync(db => db.LeaguePlayers.Where(row => row.UserId == user.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ConsentVersion, "2026-11-v1").SetProperty(row => row.Tier, LeagueTier.Silver), Ct));

        var asked = await user.Client.GetJsonAsync(LeagueClientExtensions.Me, Ct);

        Assert.True(asked.GetProperty("consentRequired").GetBoolean());
        Assert.Equal("2026-11-v2", asked.GetProperty("consent").GetProperty("version").GetString());
        Assert.Contains("Parçalarını tanıyan biri kombinin sana ait olduğunu anlayabilir.", asked.GetProperty("consent").GetProperty("text").GetString(), StringComparison.Ordinal);

        using var old = await user.Client.JoinLeagueAsync("2026-11-v1", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);

        var accepted = await user.Client.JoinLeagueOkAsync(Ct);

        Assert.False(accepted.GetProperty("consentRequired").GetBoolean());
        Assert.Equal("Silver", accepted.GetProperty("tier").GetString());
        var consents = await ConsentsAsync(user.Id);
        Assert.Equal(2, consents.Count);
        Assert.Equal(("2026-11-v2", LeagueConsent.Current.Text), (consents[^1].Version, consents[^1].Text));
        Assert.Equal("2026-11-v2", (await PlayerAsync(user.Id))!.ConsentVersion);
    }

    [Fact]
    public async Task LeavingWithoutBeingAPlayerIsForbidden()
    {
        using var stranger = await factory.CreateUserAsync(Ct);
        using var player = await factory.CreateUserAsync(Ct);
        await player.Client.JoinLeagueOkAsync(Ct);
        using (var first = await player.Client.LeaveLeagueAsync(Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using var never = await stranger.Client.LeaveLeagueAsync(Ct);
        using var twice = await player.Client.LeaveLeagueAsync(Ct);

        Assert.Equal((HttpStatusCode.Forbidden, "League.NotJoined"), (never.StatusCode, await ErrorCodeAsync(never)));
        Assert.Equal((HttpStatusCode.Forbidden, "League.NotJoined"), (twice.StatusCode, await ErrorCodeAsync(twice)));
        Assert.Equal(2, (await ConsentsAsync(player.Id)).Count);
    }

    [Fact]
    public async Task TheRoutesNeedASignedInUser()
    {
        using var anonymous = factory.CreateAnonymousClient();

        using var me = await anonymous.GetAsync(LeagueClientExtensions.Me, Ct);
        using var join = await anonymous.JoinLeagueAsync(LeagueConsent.Current.Version, Ct);
        using var leave = await anonymous.LeaveLeagueAsync(Ct);

        Assert.All([me, join, leave], response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task SwitchedOffEveryRouteAnswersNotFoundAndNothingIsWritten()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await using var off = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("League:Enabled", "false")])));
        using var client = off.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization is { } auth
            ? new AuthenticationHeaderValue(auth.Scheme, auth.Parameter)
            : null;

        using var me = await client.GetAsync(LeagueClientExtensions.Me, Ct);
        using var join = await client.JoinLeagueAsync(LeagueConsent.Current.Version, Ct);
        using var leave = await client.LeaveLeagueAsync(Ct);

        foreach (var response in new[] { me, join, leave })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("League.Disabled", await ErrorCodeAsync(response));
        }

        Assert.Null(await PlayerAsync(user.Id));
    }
}
