using System.Net;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The founder's league review page (<c>/admin/league</c>, task B8): the queue the internal endpoint reads, Restore and Remove through
/// <c>LeagueEntryReviewer</c> (which writes the audit row), the admin sign-in and the anti-forgery token, and no personal data on the page.
/// </summary>
public sealed class AdminLeagueTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A reported outfit of a new player; its reports are older than every other test's, so it is on the first page of the queue.</summary>
    private async Task<(Guid EntryId, Guid OwnerId)> ReportedEntryAsync(DateOnly day, params LeagueReportReason[] reasons)
    {
        var owner = await factory.CreatePlayerAsync(day, Ct);
        var entryId = await factory.InsertEntryAsync(day, owner.Id, cancellationToken: Ct);
        var reporters = new List<TestUser>();
        foreach (var _ in reasons)
        {
            reporters.Add(await factory.CreateUserAsync(Ct));
        }

        await factory.WithDbAsync(async db =>
        {
            for (var index = 0; index < reasons.Length; index++)
            {
                db.LeagueReports.Add(new LeagueReport
                {
                    EntryId = entryId,
                    ReporterId = reporters[index].Id,
                    Reason = reasons[index],
                    Counts = index == 0,
                    CreatedAt = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index),
                });
            }

            await db.SaveChangesAsync(Ct);

            // What the league's report endpoint keeps on the entry: the reports that count toward the hide.
            return await db.LeagueEntries.Where(entry => entry.Id == entryId).ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.CountingReports, 1), Ct);
        });
        foreach (var reporter in reporters)
        {
            reporter.Dispose();
        }

        owner.Dispose();

        return (entryId, owner.Id);
    }

    private Task<LeagueEntry> EntryAsync(Guid entryId) =>
        factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking().SingleAsync(entry => entry.Id == entryId, Ct));

    [Fact]
    public async Task ThePageListsReportedOutfitsWithTheirPiecesAndReasonsAndNothingOfTheirOwners()
    {
        var day = NewDay();
        var (reported, ownerId) = await ReportedEntryAsync(day, LeagueReportReason.Inappropriate, LeagueReportReason.NotClothing, LeagueReportReason.NotClothing);
        var unreportedOwner = await factory.CreatePlayerAsync(day, Ct);
        var unreported = await factory.InsertEntryAsync(day, unreportedOwner.Id, cancellationToken: Ct);
        var owner = await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(user => user.Id == ownerId, Ct));
        using var browser = await factory.SignedInAdminAsync(Ct);

        var html = await browser.GetHtmlAsync("/admin/league", Ct);

        var queue = await factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking().Where(entry => entry.Id == reported).Include(entry => entry.Pieces).SingleAsync(Ct));
        Assert.Contains(reported.ToString(), html, StringComparison.Ordinal);
        Assert.Contains("Reports: <strong>3</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>1</strong> count toward", html, StringComparison.Ordinal);
        Assert.Contains("Inappropriate ×1, NotClothing ×2", html, StringComparison.Ordinal);
        Assert.Contains($"day {day:yyyy-MM-dd}", html, StringComparison.Ordinal);
        Assert.Contains("Restore", html, StringComparison.Ordinal);
        Assert.Contains("Remove", html, StringComparison.Ordinal);
        foreach (var piece in queue.Pieces)
        {
            Assert.Contains($"{FakeObjectStorage.PublicBaseUrl}/{piece.ImageKey}", html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(unreported.ToString(), html, StringComparison.Ordinal);

        // No owner and no reporter on the page: no user ID, name or address, and no wardrobe object key (which holds the owner's ID).
        Assert.DoesNotContain(ownerId.ToString(), html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(owner.Email, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(owner.DisplayName, html, StringComparison.Ordinal);
        Assert.DoesNotContain("wardrobe/", html, StringComparison.Ordinal);
        foreach (var reporterId in await factory.WithDbAsync(db => db.LeagueReports.AsNoTracking().Where(report => report.EntryId == reported).Select(report => report.ReporterId).ToListAsync(Ct)))
        {
            Assert.DoesNotContain(reporterId.ToString(), html, StringComparison.OrdinalIgnoreCase);
        }

        unreportedOwner.Dispose();
    }

    [Fact]
    public async Task RestoreAndRemoveCallTheReviewerWhichWritesOneAuditRowEachWithTheAdminsEmail()
    {
        var day = NewDay();
        var (restored, _) = await ReportedEntryAsync(day, LeagueReportReason.Other);
        var (removed, _) = await ReportedEntryAsync(day, LeagueReportReason.Inappropriate);
        using var browser = await factory.SignedInAdminAsync(Ct);

        using (var restore = await browser.PostFormAsync("/admin/league", "/admin/league?handler=Restore", [new("id", restored.ToString())], Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
        }

        var afterRestore = await browser.GetHtmlAsync("/admin/league", Ct);
        Assert.Contains($"Outfit {restored.ToString()[..8]}: restore done", afterRestore, StringComparison.Ordinal);
        Assert.DoesNotContain($"title=\"{restored}\"", afterRestore, StringComparison.Ordinal);
        Assert.Contains($"title=\"{removed}\"", afterRestore, StringComparison.Ordinal);
        var entry = await EntryAsync(restored);
        Assert.Equal((LeagueEntryStatus.Competing, 0), (entry.Status, entry.CountingReports));
        Assert.NotNull(entry.ReviewedAt);

        using (var remove = await browser.PostFormAsync("/admin/league", "/admin/league?handler=Remove", [new("id", removed.ToString())], Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);
        }

        Assert.Equal(LeagueEntryStatus.Removed, (await EntryAsync(removed)).Status);
        var afterRemove = await browser.GetHtmlAsync("/admin/league", Ct);
        Assert.Contains($"Outfit {removed.ToString()[..8]}: remove done (now Removed)", afterRemove, StringComparison.Ordinal);
        Assert.DoesNotContain($"title=\"{removed}\"", afterRemove, StringComparison.Ordinal);

        // The reviewer audits (the page must not add a second row): the entity, the actions, the admin's e-mail as the actor, no personal data.
        var restoreAudit = Assert.Single(await factory.AuditOfAsync("league.entry.restore", restored.ToString(), Ct));
        var removeAudit = Assert.Single(await factory.AuditOfAsync("league.entry.remove", removed.ToString(), Ct));
        Assert.Equal((ApiFactory.AdminEmail, "league_entry"), (restoreAudit.Actor, restoreAudit.TargetType));
        Assert.Equal((ApiFactory.AdminEmail, "league_entry"), (removeAudit.Actor, removeAudit.TargetType));
        Assert.Equal(2, (await factory.WithDbAsync(db => db.AdminAuditLog.AsNoTracking().Where(row => row.TargetId == restored.ToString() || row.TargetId == removed.ToString()).ToListAsync(Ct))).Count);
    }

    [Fact]
    public async Task ARemovalIsFinalAnUnknownOutfitIsReportedAndNeitherIsAudited()
    {
        var day = NewDay();
        var (entry, _) = await ReportedEntryAsync(day, LeagueReportReason.NotClothing);
        using var browser = await factory.SignedInAdminAsync(Ct);
        using (var remove = await browser.PostFormAsync("/admin/league", "/admin/league?handler=Remove", [new("id", entry.ToString())], Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);
        }

        using (var restore = await browser.PostFormAsync("/admin/league", "/admin/league?handler=Restore", [new("id", entry.ToString())], Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
        }

        Assert.Contains("a removal is final", await browser.GetHtmlAsync("/admin/league", Ct), StringComparison.Ordinal);
        Assert.Equal(LeagueEntryStatus.Removed, (await EntryAsync(entry)).Status);
        Assert.Empty(await factory.AuditOfAsync("league.entry.restore", entry.ToString(), Ct));

        var unknown = Guid.NewGuid();
        using (var gone = await browser.PostFormAsync("/admin/league", "/admin/league?handler=Remove", [new("id", unknown.ToString())], Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, gone.StatusCode);
        }

        Assert.Contains("not found", await browser.GetHtmlAsync("/admin/league", Ct), StringComparison.Ordinal);
        Assert.Empty(await factory.AuditOfAsync("league.entry.remove", unknown.ToString(), Ct));
    }

    [Fact]
    public async Task WithoutTheAdminSignInOrTheAntiforgeryTokenNothingChanges()
    {
        var day = NewDay();
        var (entry, _) = await ReportedEntryAsync(day, LeagueReportReason.Other);

        // The page and its two handlers are the founder's: an anonymous browser and a signed-in app user are sent to the sign-in.
        using var anonymous = new AdminBrowser(factory);
        using var appUser = await factory.CreateUserAsync(Ct);
        foreach (var client in new[] { anonymous.Client, appUser.Client })
        {
            using var page = await client.GetAsync("/admin/league", Ct);
            Assert.Contains("/admin/login", page.Headers.Location?.ToString(), StringComparison.Ordinal);

            using var post = await client.PostAsync(
                "/admin/league?handler=Remove", new FormUrlEncodedContent([new KeyValuePair<string, string>("id", entry.ToString())]), Ct);
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.Contains("/admin/login", post.Headers.Location?.ToString(), StringComparison.Ordinal);
        }

        // Signed in, but with no token or a forged one: refused.
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var noToken = await browser.PostFormAsync("/admin/league", "/admin/league?handler=Remove", [new("id", entry.ToString())], Ct, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var forged = await browser.PostFormAsync(
            "/admin/league", "/admin/league?handler=Remove", [new("id", entry.ToString()), new("__AdminRequestVerificationToken", "forged")], Ct, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        Assert.Equal(LeagueEntryStatus.Competing, (await EntryAsync(entry)).Status);
        Assert.Empty(await factory.AuditOfAsync("league.entry.remove", entry.ToString(), Ct));
    }
}
