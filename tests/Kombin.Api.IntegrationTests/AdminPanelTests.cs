using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Admin;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Features.Social;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>The admin panel over HTTP: its own sign-in, the guards around it, and each page's actions with their audit rows.</summary>
public sealed class AdminPanelTests(ApiFactory factory)
{
    private static readonly string[] Pages = ["/admin", "/admin/review", "/admin/creators", "/admin/catalog", "/admin/users", "/admin/audit"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -- sign-in and guards -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryPageSendsAnAnonymousVisitorToTheLogin()
    {
        using var browser = new AdminBrowser(factory);

        foreach (var page in Pages)
        {
            using var response = await browser.Client.GetAsync(page, Ct);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/admin/login?returnUrl=", response.Headers.Location?.PathAndQuery, StringComparison.Ordinal);
        }

        using var login = await browser.Client.GetAsync("/admin/login", Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task EveryPageAnswers200ToASignedInAdminWithTheSecurityHeaders()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);

        foreach (var page in Pages)
        {
            using var response = await browser.Client.GetAsync(page, Ct);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page} answered {(int)response.StatusCode}");
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            Assert.Contains("<meta name=\"viewport\"", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SigningInSetsAHardenedSessionCookieAndAuditsIt()
    {
        using var browser = new AdminBrowser(factory);
        var before = await factory.WithDbAsync(db => db.AdminAuditLog.CountAsync(entry => entry.Action == "admin.login", Ct));

        using var response = await browser.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(header => header.StartsWith("kombin_admin=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(await factory.WithDbAsync(db => db.AdminAuditLog.CountAsync(entry => entry.Action == "admin.login", Ct)) > before);
    }

    [Fact]
    public async Task AWrongPasswordOrUnknownEmailIsRefusedAlike()
    {
        using var browser = new AdminBrowser(factory);

        using var wrong = await browser.LoginAsync(ApiFactory.AdminEmail, "not-the-password", Ct);
        using var unknown = await browser.LoginAsync("nobody@kombin.test", ApiFactory.AdminPassword, Ct);

        foreach (var response in new[] { wrong, unknown })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(cookie => cookie.StartsWith("kombin_admin=", StringComparison.Ordinal)));
            Assert.Contains("The e-mail or password is incorrect.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using var page = await browser.Client.GetAsync("/admin", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
    }

    [Fact]
    public async Task FiveFailedSignInsLockOutThatAddressEvenForTheRightPassword()
    {
        using var attacker = new AdminBrowser(factory);
        using var bystander = new AdminBrowser(factory);

        for (var attempt = 0; attempt < AdminLoginThrottle.MaxFailures; attempt++)
        {
            using var failure = await attacker.LoginAsync(ApiFactory.AdminEmail, $"wrong-{attempt}", Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        using var locked = await attacker.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.False(locked.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(cookie => cookie.StartsWith("kombin_admin=", StringComparison.Ordinal)));

        // Another address is not affected.
        await bystander.SignInAsync(Ct);
    }

    [Fact]
    public async Task APostWithoutTheAntiforgeryTokenIsRefusedAndChangesNothing()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var anonymous = new AdminBrowser(factory);
        using var author = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateProcessedPostAsync(author.Client, "borderline", Ct);

        using var noToken = await browser.PostFormAsync("/admin/review", "/admin/review?handler=Block", [new("id", postId.ToString())], Ct, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(PostStatus.NeedsReview, (await factory.PostRowAsync(postId, Ct)).Status);

        using var loginWithoutToken = await anonymous.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword, Ct, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, loginWithoutToken.StatusCode);

        using var forged = await browser.PostFormAsync(
            "/admin/review", "/admin/review?handler=Block", [new("id", postId.ToString()), new("__AdminRequestVerificationToken", "forged")], Ct, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(PostStatus.NeedsReview, (await factory.PostRowAsync(postId, Ct)).Status);
    }

    [Fact]
    public async Task TheAdminCookieAndTheAppTokenDoNotOpenEachOthersDoors()
    {
        using var admin = await factory.SignedInAdminAsync(Ct);
        using var appUser = await factory.CreateUserAsync(Ct);

        using var apiWithCookie = await admin.Client.GetAsync("/api/v1/me", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, apiWithCookie.StatusCode);

        using var panelWithToken = await appUser.Client.GetAsync("/admin", Ct);
        Assert.Equal(HttpStatusCode.Redirect, panelWithToken.StatusCode);
    }

    [Fact]
    public async Task SigningOutEndsTheSessionAndAuditsIt()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);

        using var get = await browser.Client.GetAsync("/admin/logout", Ct);
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Equal("/admin", get.Headers.Location?.OriginalString);

        using var post = await browser.PostFormAsync("/admin", "/admin/logout", [], Ct);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/admin/login", post.Headers.Location?.OriginalString);

        using var after = await browser.Client.GetAsync("/admin", Ct);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains(await factory.WithDbAsync(db => db.AdminAuditLog.Where(entry => entry.Action == "admin.logout").ToListAsync(Ct)), entry => entry.Actor == ApiFactory.AdminEmail);
    }

    // -- dashboard ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryDashboardQueryRunsAndTheSqlIsOnThePage()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var user = await factory.CreateUserAsync(Ct);

        var data = await factory.WithServiceAsync<AdminDashboard, DashboardData>(dashboard => dashboard.LoadAsync(Ct));

        Assert.Equal(DashboardQueries.Metrics.Select(metric => metric.Key), data.Figures.Select(figure => figure.Metric.Key));
        var newUsers = data.Figures.Single(figure => figure.Metric.Key == "new_users");
        Assert.True(newUsers.Today >= 1);
        Assert.True(newUsers.Week >= newUsers.Today);
        Assert.True(data.Health.Vision.IsHealthy);
        Assert.True(data.Health.DatabaseBytes > 0);
        Assert.False(data.Health.HangfireEnabled);
        Assert.Single(data.Breakdowns);

        var html = await browser.GetHtmlAsync("/admin", Ct);
        Assert.Contains("<details><summary>SQL</summary>", html, StringComparison.Ordinal);
        Assert.Contains("FROM identity.users WHERE created_at &gt;= @since", html, StringComparison.Ordinal);
        Assert.Contains("Hangfire is switched off", html, StringComparison.Ordinal);
    }

    // -- review -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ApprovingAPostUnderReviewPublishesItAndWritesTheAuditRow()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var author = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateProcessedPostAsync(author.Client, "borderline", Ct);
        await author.Client.PublishAsync(postId, "Public", [], "Review me", Ct);
        Assert.Equal(PostStatus.NeedsReview, (await factory.PostRowAsync(postId, Ct)).Status);

        var page = await browser.GetHtmlAsync("/admin/review", Ct);
        Assert.Contains(postId.ToString(), page, StringComparison.Ordinal);
        Assert.Contains("Review me", page, StringComparison.Ordinal);

        using var response = await browser.PostFormAsync("/admin/review", "/admin/review?handler=Approve", [new("id", postId.ToString())], Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var row = await factory.PostRowAsync(postId, Ct);
        Assert.Equal(PostStatus.Live, row.Status);
        Assert.NotNull(row.ReviewedAt);
        var audit = Assert.Single(await factory.AuditOfAsync("post.approve", postId.ToString(), Ct));
        Assert.Equal((ApiFactory.AdminEmail, "post"), (audit.Actor, audit.TargetType));
        Assert.Contains("Live", audit.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockingAPostTellsTheAuthorTheChosenReasonAndRefusesToDecideTwice()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var author = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateProcessedPostAsync(author.Client, "borderline", Ct);

        using var response = await browser.PostFormAsync(
            "/admin/review", "/admin/review?handler=Block", [new("id", postId.ToString()), new("reasonKey", "spam"), new("custom", "Tekrarlanan paylaşım.")], Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var row = await factory.PostRowAsync(postId, Ct);
        Assert.Equal(PostStatus.Blocked, row.Status);
        Assert.Equal(BlockReasons.Compose("spam", "Tekrarlanan paylaşım."), row.ModerationReason);
        Assert.Contains("spam", row.ModerationReason, StringComparison.OrdinalIgnoreCase);
        var audit = Assert.Single(await factory.AuditOfAsync("post.block", postId.ToString(), Ct));
        Assert.Contains(row.ModerationReason!, audit.Summary, StringComparison.Ordinal);

        var mine = await author.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Equal("Blocked", mine.GetProperty("status").GetString());

        // It left the queue: a second decision is refused with the domain's error and writes no audit row.
        using var again = await browser.PostFormAsync("/admin/review", "/admin/review?handler=Approve", [new("id", postId.ToString())], Ct);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Equal(PostStatus.Blocked, (await factory.PostRowAsync(postId, Ct)).Status);
        Assert.Empty(await factory.AuditOfAsync("post.approve", postId.ToString(), Ct));
    }

    [Fact]
    public async Task APostHiddenByReportsShowsItsReportsAndApprovingRestartsTheCount()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var author = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateLivePostAsync(author.Client, cancellationToken: Ct);

        for (var index = 0; index < PostRules.HideAtReports; index++)
        {
            using var reporter = await factory.CreateUserAsync(Ct);
            using var report = await reporter.Client.ReportAsync(postId, index == 0 ? "Nudity" : "Spam", Ct);
            Assert.Equal(HttpStatusCode.Accepted, report.StatusCode);
        }

        Assert.Equal(PostStatus.Hidden, (await factory.PostRowAsync(postId, Ct)).Status);
        var page = await browser.GetHtmlAsync("/admin/review", Ct);
        Assert.Contains(postId.ToString(), page, StringComparison.Ordinal);
        Assert.Contains("Nudity ×1", page, StringComparison.Ordinal);
        Assert.Contains("Spam ×2", page, StringComparison.Ordinal);

        using var response = await browser.PostFormAsync("/admin/review", "/admin/review?handler=Approve", [new("id", postId.ToString())], Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var row = await factory.PostRowAsync(postId, Ct);
        Assert.Equal((PostStatus.Live, 0), (row.Status, row.ReportCount));
    }

    [Fact]
    public async Task KeepingAPostChangesNoStatusAndMovesItBehindTheOthers()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var author = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateProcessedPostAsync(author.Client, "borderline", Ct);
        var before = await factory.PostRowAsync(postId, Ct);

        using var response = await browser.PostFormAsync("/admin/review", "/admin/review?handler=Keep", [new("id", postId.ToString())], Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var after = await factory.PostRowAsync(postId, Ct);
        Assert.Equal((PostStatus.NeedsReview, before.ReportCount, before.ReviewedAt), (after.Status, after.ReportCount, after.ReviewedAt));
        Assert.True(after.UpdatedAt > before.UpdatedAt);
        Assert.Single(await factory.AuditOfAsync("post.keep", postId.ToString(), Ct));
    }

    [Fact]
    public async Task TheInternalReviewEndpointGainedKeepAndAReason()
    {
        using var anonymous = factory.CreateAnonymousClient();
        using var author = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateProcessedPostAsync(author.Client, "borderline", Ct);

        using var keep = await anonymous.PostInternalAsync($"/api/v1/internal/social/posts/{postId}/review", new { decision = "Keep" }, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        Assert.Equal(PostStatus.NeedsReview, (await factory.PostRowAsync(postId, Ct)).Status);

        using var tooLong = await anonymous.PostInternalAsync($"/api/v1/internal/social/posts/{postId}/review", new { decision = "Remove", reason = new string('x', 201) }, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using var remove = await anonymous.PostInternalAsync($"/api/v1/internal/social/posts/{postId}/review", new { decision = "Remove", reason = "Kendi nedenimiz." }, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        var row = await factory.PostRowAsync(postId, Ct);
        Assert.Equal((PostStatus.Blocked, "Kendi nedenimiz."), (row.Status, row.ModerationReason));
    }

    // -- creators -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFounderCanInviteACreatorFoundByEmailAndRevokeThem()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var user = await factory.CreateUserAsync(Ct);
        var email = await factory.WithDbAsync(db => db.Users.Where(candidate => candidate.Id == user.Id).Select(candidate => candidate.Email).SingleAsync(Ct));
        var handle = $"c{Guid.NewGuid():N}"[..14];

        var search = await browser.GetHtmlAsync($"/admin/creators?q={Uri.EscapeDataString(email)}", Ct);
        Assert.Contains(user.Id.ToString(), search, StringComparison.Ordinal);
        Assert.Contains(AdminFormat.MaskEmail(email), search, StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(search, System.Text.RegularExpressions.Regex.Escape(email))); // only the search box echoes what the founder typed

        using var invalid = await browser.PostFormAsync("/admin/creators", "/admin/creators?handler=Invite", [new("userId", user.Id.ToString()), new("handle", "x!"), new("bio", ""), new("link", "")], Ct);
        Assert.Equal(HttpStatusCode.Redirect, invalid.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.CreatorProfiles.AnyAsync(creator => creator.UserId == user.Id, Ct)));

        using var invite = await browser.PostFormAsync(
            "/admin/creators", "/admin/creators?handler=Invite",
            [new("userId", user.Id.ToString()), new("handle", $"@{handle.ToUpperInvariant()}"), new("bio", "Stil önerileri"), new("link", "https://example.com/c")], Ct);
        Assert.Equal(HttpStatusCode.Redirect, invite.StatusCode);
        var creator = await factory.WithDbAsync(db => db.CreatorProfiles.AsNoTracking().SingleAsync(candidate => candidate.UserId == user.Id, Ct));
        Assert.Equal((handle, true, "Stil önerileri"), (creator.Handle, creator.IsActive, creator.Bio));
        Assert.Contains($"handle={handle}", Assert.Single(await factory.AuditOfAsync("creator.invite", user.Id.ToString(), Ct)).Summary, StringComparison.Ordinal);
        Assert.Contains($"@{handle}", await browser.GetHtmlAsync("/admin/creators", Ct), StringComparison.Ordinal);

        using var anonymous = factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/v1/creators/{handle}", Ct)).StatusCode);

        using var revoke = await browser.PostFormAsync("/admin/creators", "/admin/creators?handler=Revoke", [new("userId", user.Id.ToString())], Ct);
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        Assert.False((await factory.WithDbAsync(db => db.CreatorProfiles.AsNoTracking().SingleAsync(candidate => candidate.UserId == user.Id, Ct))).IsActive);
        Assert.Single(await factory.AuditOfAsync("creator.revoke", user.Id.ToString(), Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/creators/{handle}", Ct)).StatusCode);
    }

    [Fact]
    public async Task ATakenHandleIsRefusedAndTheRevokeEndpointIsInternalOnly()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var first = await factory.CreateUserAsync(Ct);
        using var second = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var handle = $"t{Guid.NewGuid():N}"[..14];

        using var invite = await browser.PostFormAsync("/admin/creators", "/admin/creators?handler=Invite", [new("userId", first.Id.ToString()), new("handle", handle), new("bio", ""), new("link", "")], Ct);
        Assert.Equal(HttpStatusCode.Redirect, invite.StatusCode);
        using var clash = await browser.PostFormAsync("/admin/creators", "/admin/creators?handler=Invite", [new("userId", second.Id.ToString()), new("handle", handle), new("bio", ""), new("link", "")], Ct);
        Assert.Equal(HttpStatusCode.Redirect, clash.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.CreatorProfiles.AnyAsync(creator => creator.UserId == second.Id, Ct)));
        Assert.Empty(await factory.AuditOfAsync("creator.invite", second.Id.ToString(), Ct));

        using var noKey = await anonymous.DeleteAsync($"/api/v1/internal/social/creators/{first.Id}", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        using var revoked = await anonymous.SendInternalAsync(HttpMethod.Delete, $"/api/v1/internal/social/creators/{first.Id}", null, Ct);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.False((await revoked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("isActive").GetBoolean());
        using var notACreator = await anonymous.SendInternalAsync(HttpMethod.Delete, $"/api/v1/internal/social/creators/{second.Id}", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, notACreator.StatusCode);
    }

    // -- users --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisablingAnAccountRefusesSignInAndRefreshAndEnablingRestoresIt()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var client = factory.CreateAnonymousClient();
        var email = ApiClientExtensions.NewEmail();
        var tokens = await client.RegisterAndReadAsync(email, Ct);
        var userId = await factory.WithDbAsync(db => db.Users.Where(candidate => candidate.Email == email).Select(candidate => candidate.Id).SingleAsync(Ct));

        var search = await browser.GetHtmlAsync($"/admin/users?q={Uri.EscapeDataString(email)}", Ct);
        Assert.Contains(email, search, StringComparison.Ordinal);
        Assert.Contains("Disable account", search, StringComparison.Ordinal);

        using var disable = await browser.PostFormAsync($"/admin/users?id={userId}", "/admin/users?handler=Disable", [new("id", userId.ToString())], Ct);
        Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);
        Assert.NotNull((await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId, Ct))).DisabledAt);
        Assert.Equal(0, await factory.WithDbAsync(db => db.RefreshTokens.CountAsync(token => token.UserId == userId && token.RevokedAt == null, Ct)));
        Assert.Single(await factory.AuditOfAsync("user.disable", userId.ToString(), Ct));

        using var login = await client.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("Identity.AccountDisabled", (await login.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        using var wrongPassword = await client.LoginAsync(email, "Wrong-password-1", cancellationToken: Ct);
        Assert.Equal("Identity.InvalidCredentials", (await wrongPassword.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        using var refresh = await client.RefreshAsync(tokens.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        using var enable = await browser.PostFormAsync($"/admin/users?id={userId}", "/admin/users?handler=Enable", [new("id", userId.ToString())], Ct);
        Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);
        Assert.Null((await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId, Ct))).DisabledAt);
        using var again = await client.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(await factory.AuditOfAsync("user.enable", userId.ToString(), Ct));
    }

    [Fact]
    public async Task ADisabledAccountCannotSignInWithGoogleEitherAndTheInternalEndpointsDoTheSame()
    {
        using var client = factory.CreateAnonymousClient();
        var email = ApiClientExtensions.NewEmail();
        var token = FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), email);
        using var first = await client.ExternalSignInAsync("google", token, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var userId = await factory.WithDbAsync(db => db.Users.Where(candidate => candidate.Email == email).Select(candidate => candidate.Id).SingleAsync(Ct));

        using var noKey = await client.PostAsync($"/api/v1/internal/identity/users/{userId}/disable", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        using var disabled = await client.SendInternalAsync(HttpMethod.Post, $"/api/v1/internal/identity/users/{userId}/disable", null, Ct);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.True((await disabled.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("revokedTokens").GetInt32() >= 1);

        using var refused = await client.ExternalSignInAsync("google", token, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Identity.AccountDisabled", (await refused.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var enabled = await client.SendInternalAsync(HttpMethod.Post, $"/api/v1/internal/identity/users/{userId}/enable", null, Ct);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var back = await client.ExternalSignInAsync("google", token, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);

        using var unknown = await client.SendInternalAsync(HttpMethod.Post, $"/api/v1/internal/identity/users/{Guid.NewGuid()}/disable", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task TheUserPageShowsCountsAndNothingSensitive()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.Red, cancellationToken: Ct);
        await factory.CreateLivePostAsync(user.Client, cancellationToken: Ct);

        var html = await browser.GetHtmlAsync($"/admin/users?id={user.Id}", Ct);

        Assert.Matches(@"Wardrobe items</th><td>1</td>", html);
        Assert.Matches(@"Posts</th><td>1 \(1 live\)</td>", html);
        Assert.DoesNotContain("measurement", html, StringComparison.OrdinalIgnoreCase);
    }

    // -- catalog ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBackfillButtonQueuesTheEmbeddingJobOnceAndAuditsIt()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        const int Limit = 321;

        using var response = await browser.PostFormAsync("/admin/catalog", "/admin/catalog?handler=Backfill", [new("limit", Limit.ToString(CultureInfo.InvariantCulture))], Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var queued = factory.Jobs.Jobs.Where(job => job.Type == typeof(ProductEmbeddingJob) && job.Method.Name == nameof(ProductEmbeddingJob.BackfillAsync) && job.Args[0] is Limit).ToList();
        Assert.Single(queued);
        var audit = (await factory.WithDbAsync(db => db.AdminAuditLog.Where(entry => entry.Action == "catalog.backfill" && entry.Summary!.StartsWith("limit=321;")).ToListAsync(Ct))).Single();
        Assert.Equal(ApiFactory.AdminEmail, audit.Actor);
    }

    [Fact]
    public async Task TheCatalogPageListsSellersAndShowsAProductWithItsOffersAndPriceHistory()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var seeded = await factory.SeedProductAsync(anonymous, 349m, cancellationToken: Ct);
        var slug = await factory.WithDbAsync(db => db.Products.Where(product => product.Id == seeded.ProductId).Select(product => product.Slug).SingleAsync(Ct));
        var offerUrl = await factory.WithDbAsync(db => db.Offers.Where(offer => offer.Id == seeded.OfferId).Select(offer => offer.Url).SingleAsync(Ct));

        var overview = await browser.GetHtmlAsync("/admin/catalog", Ct);
        Assert.Contains(seeded.SellerSlug, overview, StringComparison.Ordinal);
        Assert.Contains("raw seller URL", overview, StringComparison.Ordinal);

        var byId = await browser.GetHtmlAsync($"/admin/catalog?q={seeded.ProductId}", Ct);
        Assert.Contains("Price history", byId, StringComparison.Ordinal);
        Assert.Contains("349.00 ₺", byId, StringComparison.Ordinal);
        Assert.Contains(offerUrl, byId, StringComparison.Ordinal);
        Assert.Contains("Seller URL (raw, admin only)", byId, StringComparison.Ordinal);

        // A bare slug is shared by many brands in a populated catalog; with the brand it names one product.
        var brandSlug = await factory.WithDbAsync(db => db.Products.Where(product => product.Id == seeded.ProductId).Select(product => product.Brand.Slug).SingleAsync(Ct));
        var bySlug = await browser.GetHtmlAsync($"/admin/catalog?q={Uri.EscapeDataString($"{brandSlug}/{slug}")}", Ct);
        Assert.Contains("Price history", bySlug, StringComparison.Ordinal);
        Assert.Contains(seeded.ProductId.ToString(), bySlug, StringComparison.Ordinal);
        Assert.Contains("Find a product", await browser.GetHtmlAsync($"/admin/catalog?q={Uri.EscapeDataString(slug)}", Ct), StringComparison.Ordinal);
    }

    // -- audit --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAuditPageIsPagedNewestFirst()
    {
        using var browser = await factory.SignedInAdminAsync(Ct);
        var marker = $"marker-{Guid.NewGuid():N}";
        await factory.WithServiceAsync<AdminAuditLog, int>(async audit =>
        {
            await audit.RecordAsync(ApiFactory.AdminEmail, "test.marker", "test", marker, "paging check", Ct);

            return 0;
        });

        var first = await browser.GetHtmlAsync("/admin/audit", Ct);
        Assert.Contains(marker, first, StringComparison.Ordinal);
        Assert.Contains("paging check", first, StringComparison.Ordinal);

        var outOfRange = await browser.GetHtmlAsync("/admin/audit?p=99999", Ct);
        Assert.Contains("Nothing yet.", outOfRange, StringComparison.Ordinal);
    }
}
