using Kombin.Api.Features.League;
using Kombin.Api.Features.League.Jobs;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// <c>league-purge</c> (<c>docs/design/league.md</c>, section 13; <c>docs/privacy-notes.md</c> 6b): copies 7 days after their day, votes and the
/// voters' counters 30 days after a closed day, entries 90 days after a closed day, and copies nobody names once they are a day old. The
/// retention steps run on the test's own days; the orphan sweep only ever finds what the test backdated (every other copy is younger).
/// </summary>
[Collection(LeagueCloses.Name)]
public sealed class LeaguePurgeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<LeaguePurgeSummary> PurgeAsync(DateTimeOffset now, IReadOnlyCollection<DateOnly> days, bool orphans = false) =>
        factory.RunJobAsync<LeaguePurgeJob, LeaguePurgeSummary>(job => job.PurgeAsync(now, days, orphans, Ct));

    private Task<List<string?>> KeysAsync(Guid entryId) =>
        factory.WithDbAsync(db => db.LeagueEntryPieces.Where(piece => piece.EntryId == entryId).OrderBy(piece => piece.Position).Select(piece => piece.ImageKey).ToListAsync(Ct));

    [Fact]
    public async Task CopiesVotesAndEntriesLeaveOnTheirScheduleAndOnlyForClosedDays()
    {
        var closed = NewDay();
        var open = closed.AddDays(1);
        var days = new[] { closed, open };

        var player = await factory.CreatePlayerAsync(closed, Ct);
        var other = await factory.CreatePlayerAsync(closed, Ct);
        var closedEntry = await factory.InsertEntryAsync(closed, player.Id, cancellationToken: Ct);
        var openEntry = await factory.InsertEntryAsync(open, player.Id, cancellationToken: Ct);
        var closedEngine = await factory.InsertEntryAsync(closed, null, cancellationToken: Ct);
        var openEngine = await factory.InsertEntryAsync(open, null, cancellationToken: Ct);
        var closedKeys = (await KeysAsync(closedEntry)).Select(key => key!).ToList();
        var openKeys = (await KeysAsync(openEntry)).Select(key => key!).ToList();
        foreach (var key in closedKeys.Concat(openKeys))
        {
            factory.Storage.Upload(key, Thumbnail, "image/webp");
        }

        await factory.InsertVotesAsync(closed, other.Id, [(closedEntry, closedEngine, LeaguePairChoice.Left, true)], Ct);
        await factory.InsertVotesAsync(open, other.Id, [(openEntry, openEngine, LeaguePairChoice.Left, true)], Ct);
        await factory.WithDbAsync(async db =>
        {
            db.LeagueVoterDays.AddRange(days.Select(day => new LeagueVoterDay { UserId = other.Id, Day = day, Served = 1, Counted = 1 }));
            return await db.SaveChangesAsync(Ct);
        });
        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(closed, Time(closed, 21, 1), Ct)).Outcome);

        // Six days later nothing is due.
        Assert.Equal(new LeaguePurgeSummary(0, 0, 0, 0, 0), await PurgeAsync(Time(closed.AddDays(6), 4, 15), days));

        // Seven days after the closed day: its copies go (objects first, then the keys); the open day's are a day younger.
        var week = await PurgeAsync(Time(closed.AddDays(7), 4, 15), days);
        Assert.Equal(new LeaguePurgeSummary(2, 0, 0, 0, 0), week);
        Assert.All(closedKeys, key => Assert.Null(factory.Storage.Find(key)));
        Assert.All(await KeysAsync(closedEntry), Assert.Null);
        Assert.All(openKeys, key => Assert.NotNull(factory.Storage.Find(key)));

        // Thirty days: the closed day's votes and counters go; the open day keeps its votes until it is closed (its copies are old enough now).
        var month = await PurgeAsync(Time(closed.AddDays(30), 4, 15), days);
        Assert.Equal(new LeaguePurgeSummary(2, 1, 1, 0, 0), month);
        Assert.False(await factory.WithDbAsync(db => db.LeaguePairs.AnyAsync(pair => pair.Day == closed && pair.VoterId == other.Id, Ct)));
        Assert.True(await factory.WithDbAsync(db => db.LeaguePairs.AnyAsync(pair => pair.Day == open && pair.VoterId == other.Id, Ct)));
        Assert.True(await factory.WithDbAsync(db => db.LeagueVoterDays.AnyAsync(row => row.Day == open && row.UserId == other.Id, Ct)));

        // Ninety days: the closed day's entries go; its score rows stay, without the entry.
        var quarter = await PurgeAsync(Time(closed.AddDays(90), 4, 15), days);
        Assert.Equal(new LeaguePurgeSummary(0, 0, 0, 2, 0), quarter);
        Assert.False(await factory.WithDbAsync(db => db.LeagueEntries.AnyAsync(entry => entry.Day == closed, Ct)));
        Assert.Equal(2, await factory.WithDbAsync(db => db.LeagueEntries.CountAsync(entry => entry.Day == open, Ct)));
        var score = await factory.WithDbAsync(db => db.LeagueDailyScores.AsNoTracking().SingleAsync(row => row.UserId == player.Id && row.Day == closed, Ct));
        Assert.True(score.Competed);
        Assert.Null(score.EntryId);
    }

    /// <summary>A copy nobody names (a crash between the object write and the commit) is deleted once it is a day old; a younger one and a named one stay.</summary>
    [Fact]
    public async Task CopiesNoRowNamesAreSweptOnceTheyAreADayOld()
    {
        var day = NewDay();
        var player = await factory.CreatePlayerAsync(day, Ct);
        var entryId = await factory.InsertEntryAsync(day, player.Id, cancellationToken: Ct);
        var named = (await KeysAsync(entryId))[0]!;
        var orphan = $"league/{Guid.NewGuid():D}/{Guid.NewGuid():D}/thumb.webp";
        var fresh = $"league/{Guid.NewGuid():D}/{Guid.NewGuid():D}/thumb.webp";
        var elsewhere = $"wardrobe/{Guid.NewGuid():D}/{Guid.NewGuid():D}/thumb.webp";
        foreach (var key in new[] { named, orphan, fresh, elsewhere })
        {
            factory.Storage.Upload(key, Thumbnail, "image/webp");
        }

        var now = TestClock.UtcNow;
        factory.Storage.Backdate(named, now.AddDays(-3));
        factory.Storage.Backdate(orphan, now.AddDays(-1).AddMinutes(-1));
        factory.Storage.Backdate(elsewhere, now.AddDays(-3));
        factory.Storage.Backdate(fresh, now.AddHours(-23));

        var summary = await PurgeAsync(now, [], orphans: true);

        Assert.True(summary.OrphansDeleted >= 1);
        Assert.Null(factory.Storage.Find(orphan));
        Assert.Contains(orphan, factory.Storage.Deleted);
        Assert.NotNull(factory.Storage.Find(named));
        Assert.NotNull(factory.Storage.Find(fresh));
        Assert.NotNull(factory.Storage.Find(elsewhere));
    }
}
