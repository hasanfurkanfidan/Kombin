using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Identity;
using Kombin.Api.Features.Identity.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Notification;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Account deletion end to end: <c>DELETE /me</c>, <c>POST /auth/cancel-deletion</c>, the <c>account-deletion</c> job and the admin panel's two
/// buttons. The matrix test fills every table that belongs to a user and checks each one after the purge; the job is called directly with the
/// user's ID because the database is shared.
/// </summary>
public sealed class AccountDeletionTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Wrong => "Wrong-password-1";

    private static async Task<HttpResponseMessage> DeleteMeAsync(HttpClient client, string? password, string? confirm = "SİL")
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me") { Content = JsonContent.Create(new { password, confirm }) };

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The database keeps microseconds, the request clock keeps ticks.</summary>
    private static void AssertSameInstant(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.InRange((expected - actual).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

    private static async Task<string> EmailOfAsync(HttpClient client) => (await client.GetJsonAsync("/api/v1/me", Ct)).GetProperty("email").GetString()!;

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/cancel-deletion", new { email, password }, Ct);

    private Task<int> BackdateAsync(Guid userId, int days) =>
        factory.WithDbAsync(db => db.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(
            setters => setters.SetProperty(user => user.DeletionRequestedAt, DateTimeOffset.UtcNow.AddDays(-days)), Ct));

    private Task<AccountDeletionSummary> RunJobAsync(params Guid[] userIds) =>
        factory.RunJobAsync<AccountDeletionJob, AccountDeletionSummary>(job => job.DeleteDueAsync(userIds, Ct));

    private Task<User?> UserRowAsync(Guid userId) =>
        factory.WithDbAsync(db => db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, Ct));

    private async Task<(TestUser User, string Email)> NewUserAsync()
    {
        var user = await factory.CreateUserAsync(Ct);

        return (user, await EmailOfAsync(user.Client));
    }

    private async Task<(TestUser User, string Subject, string Token)> NewSocialUserAsync()
    {
        var client = factory.CreateAnonymousClient();
        var subject = FakeExternalTokenVerifier.NewSubject();
        var token = FakeExternalTokenVerifier.Token(subject, ApiClientExtensions.NewEmail(), name: "Social Deleter");
        using var response = await client.ExternalSignInAsync("google", token, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.ReadTokensAsync(Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetJsonAsync("/api/v1/me", Ct);

        return (new TestUser(client, me.GetProperty("id").GetGuid()), subject, token);
    }

    [Fact]
    public async Task DeletingNeedsTheConfirmationWordAndAPasswordAccountsOwnPassword()
    {
        var (user, _) = await NewUserAsync();
        using var _1 = user;

        using var noConfirm = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword, confirm: null);
        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        var problem = await noConfirm.ReadProblemAsync(Ct);
        Assert.Equal("Validation.Failed", problem.GetProperty("errorCode").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("confirm", out _));

        using var wrongWord = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword, confirm: "delete");
        Assert.Equal(HttpStatusCode.BadRequest, wrongWord.StatusCode);

        using var noPassword = await DeleteMeAsync(user.Client, password: null);
        Assert.Equal(HttpStatusCode.BadRequest, noPassword.StatusCode);
        Assert.Equal("Identity.PasswordRequired", (await noPassword.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var wrongPassword = await DeleteMeAsync(user.Client, Wrong);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPassword.StatusCode);
        Assert.Equal("Identity.PasswordIncorrect", (await wrongPassword.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Null((await UserRowAsync(user.Id))!.DeletionRequestedAt);

        // The Turkish word in lower case with a plain "i" is what a phone keyboard gives; it counts.
        using var accepted = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword, confirm: "sil");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var requestedAt = body.GetProperty("deletionRequestedAt").GetDateTimeOffset();
        var scheduledFor = body.GetProperty("deletionScheduledFor").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromDays(7), scheduledFor - requestedAt);
        Assert.InRange(DateTimeOffset.UtcNow - requestedAt, TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AGoogleOnlyAccountNeedsNoPassword()
    {
        var (user, _, _) = await NewSocialUserAsync();
        using var _1 = user;

        using var accepted = await DeleteMeAsync(user.Client, password: null);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.NotNull((await UserRowAsync(user.Id))!.DeletionRequestedAt);
    }

    [Fact]
    public async Task ARequestEndsEverySessionAndDeviceHidesEveryPostAndRefusesSignInWithTheDate()
    {
        var (user, email) = await NewUserAsync();
        using var _1 = user;
        using var anonymous = factory.CreateAnonymousClient();
        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        var publicPost = await factory.CreateLivePostAsync(user.Client, "Public", cancellationToken: Ct);
        var followersPost = await factory.CreateLivePostAsync(user.Client, "Followers", cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, await anonymous.StatusOfAsync(publicPost, Ct));

        // The refresh token the app holds from registering.
        using var login = await anonymous.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        var session = await login.ReadTokensAsync(Ct);

        using var requested = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var first = (await requested.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("deletionRequestedAt").GetDateTimeOffset();

        // Posts: gone for everyone at once (404), and held for a possible cancellation.
        Assert.Equal(HttpStatusCode.NotFound, await anonymous.StatusOfAsync(publicPost, Ct));
        Assert.Equal(HttpStatusCode.NotFound, await anonymous.StatusOfAsync(followersPost, Ct));
        Assert.All(
            await factory.WithDbAsync(db => db.Posts.AsNoTracking().Where(post => post.AuthorId == user.Id).ToListAsync(Ct)),
            post => Assert.Equal((PostStatus.AccountPending, PostStatus.Live), (post.Status, post.StatusBeforeDeletion)));

        // Nobody else can find the account while it is pending: no profile, no posts, nothing to follow.
        using var viewer = await factory.CreateUserAsync(Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Client.GetAsync($"/api/v1/users/{user.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Client.GetAsync($"/api/v1/users/{user.Id}/posts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Client.PostAsync($"/api/v1/users/{user.Id}/follow", null, Ct)).StatusCode);

        // Sessions and devices.
        using var refresh = await anonymous.RefreshAsync(session.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.RefreshTokens.CountAsync(row => row.UserId == user.Id && row.RevokedAt == null, Ct)));
        var device = Assert.Single(await factory.DevicesWithTokenAsync(token, Ct));
        Assert.False(device.IsActive);
        Assert.Equal(Device.AccountDeletion, device.DeactivationReason);

        // Signing in says why, with the date, only once the password was right.
        using var signIn = await anonymous.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        var problem = await signIn.ReadProblemAsync(Ct);
        Assert.Equal("Identity.AccountPendingDeletion", problem.GetProperty("errorCode").GetString());
        AssertSameInstant(first.AddDays(7), problem.GetProperty("deletionScheduledFor").GetDateTimeOffset());
        using var wrong = await anonymous.LoginAsync(email, Wrong, cancellationToken: Ct);
        Assert.Equal("Identity.InvalidCredentials", (await wrong.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        // Asking again never moves the date.
        using var again = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        AssertSameInstant(first, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("deletionRequestedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task CancellingWithinTheWindowRestoresSignInAndTheHiddenPosts()
    {
        var (user, email) = await NewUserAsync();
        using var _1 = user;
        using var anonymous = factory.CreateAnonymousClient();
        var postId = await factory.CreateLivePostAsync(user.Client, "Public", cancellationToken: Ct);
        using var requested = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword);
        Assert.Equal(HttpStatusCode.NotFound, await anonymous.StatusOfAsync(postId, Ct));

        using var wrong = await CancelAsync(anonymous, email, Wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Identity.InvalidCredentials", (await wrong.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        using var unknown = await CancelAsync(anonymous, ApiClientExtensions.NewEmail(), Wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await anonymous.StatusOfAsync(postId, Ct));

        using var cancelled = await CancelAsync(anonymous, email, ApiClientExtensions.DefaultPassword);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var tokens = await cancelled.ReadTokensAsync(Ct);
        Assert.False(tokens.IsNewUser);

        // The tokens work, the account is normal again and the post is back as it was.
        using var me = await anonymous.GetMeAsync(tokens.AccessToken, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Null((await UserRowAsync(user.Id))!.DeletionRequestedAt);
        Assert.Equal(HttpStatusCode.OK, await anonymous.StatusOfAsync(postId, Ct));
        using var profile = await anonymous.GetAsync($"/api/v1/users/{user.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var row = await factory.PostRowAsync(postId, Ct);
        Assert.Equal((PostStatus.Live, (PostStatus?)null), (row.Status, row.StatusBeforeDeletion));
        using var login = await anonymous.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var twice = await CancelAsync(anonymous, email, ApiClientExtensions.DefaultPassword);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("Identity.DeletionNotPending", (await twice.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AGoogleAccountCancelsWithItsIdToken()
    {
        var (user, _, token) = await NewSocialUserAsync();
        using var _1 = user;
        using var anonymous = factory.CreateAnonymousClient();
        using var requested = await DeleteMeAsync(user.Client, password: null);
        using var signIn = await anonymous.ExternalSignInAsync("google", token, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        Assert.Equal("Identity.AccountPendingDeletion", (await signIn.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var stranger = await anonymous.PostAsJsonAsync("/api/v1/auth/cancel-deletion", new { provider = "google", idToken = FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), ApiClientExtensions.NewEmail()) }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
        using var invalid = await anonymous.PostAsJsonAsync("/api/v1/auth/cancel-deletion", new { provider = "google", idToken = "invalid" }, Ct);
        Assert.Equal("Identity.ExternalTokenInvalid", (await invalid.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var cancelled = await anonymous.PostAsJsonAsync("/api/v1/auth/cancel-deletion", new { provider = "google", idToken = token }, Ct);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Null((await UserRowAsync(user.Id))!.DeletionRequestedAt);
        using var after = await anonymous.ExternalSignInAsync("google", token, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task CancelRequiresOneKindOfCredentials()
    {
        using var anonymous = factory.CreateAnonymousClient();

        using var nothing = await anonymous.PostAsJsonAsync("/api/v1/auth/cancel-deletion", new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);

        using var badProvider = await anonymous.PostAsJsonAsync("/api/v1/auth/cancel-deletion", new { provider = "facebook", idToken = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badProvider.StatusCode);
    }

    [Fact]
    public async Task CancellingAfterTheWindowIsRefusedAndTheAccountStaysPending()
    {
        var (user, email) = await NewUserAsync();
        using var _1 = user;
        using var anonymous = factory.CreateAnonymousClient();
        using var requested = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword);
        await BackdateAsync(user.Id, 8);

        using var cancelled = await CancelAsync(anonymous, email, ApiClientExtensions.DefaultPassword);

        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        Assert.Equal("Identity.DeletionWindowClosed", (await cancelled.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.NotNull((await UserRowAsync(user.Id))!.DeletionRequestedAt);
    }

    [Fact]
    public async Task TheJobLeavesAccountsInsideTheWindowAloneAndDeletesTheOthersOnce()
    {
        var (user, email) = await NewUserAsync();
        using var _1 = user;
        using var anonymous = factory.CreateAnonymousClient();
        using var requested = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword);

        await BackdateAsync(user.Id, 6);
        Assert.Equal(0, (await RunJobAsync(user.Id)).AccountsDeleted);
        Assert.NotNull(await UserRowAsync(user.Id));

        await BackdateAsync(user.Id, 8);
        var summary = await RunJobAsync(user.Id);
        Assert.Equal(1, summary.AccountsDeleted);
        Assert.Null(await UserRowAsync(user.Id));

        // Nothing is left to sign in to, and a second run has nothing to do.
        using var login = await anonymous.LoginAsync(email, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("Identity.InvalidCredentials", (await login.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Equal(0, (await RunJobAsync(user.Id)).AccountsDeleted);
    }

    [Fact]
    public async Task AStorageFailurePostponesTheDeletionAndTheNextRunFinishesIt()
    {
        var (user, _) = await NewUserAsync();
        using var _1 = user;
        var itemId = await factory.AddPhotoItemAsync(user.Client, cancellationToken: Ct);
        var item = await factory.ItemRowAsync(itemId, Ct);
        var keys = new[] { item.ImageOriginalKey!, item.ImageCutoutKey!, item.ImageThumbKey! };
        Assert.All(keys, key => Assert.NotNull(factory.Storage.Find(key)));
        using var requested = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword);
        await BackdateAsync(user.Id, 8);

        var prefix = $"wardrobe/{user.Id}/";
        factory.Storage.FailDeletesUnder(prefix);
        var failed = await RunJobAsync(user.Id);

        // Nothing was touched: the account, its rows and its objects are all still there for the retry.
        Assert.Equal((0, 1), (failed.AccountsDeleted, failed.StorageFailures));
        Assert.NotNull(await UserRowAsync(user.Id));
        Assert.Equal(1, await factory.WithDbAsync(db => db.WardrobeItems.CountAsync(row => row.UserId == user.Id, Ct)));
        Assert.All(keys, key => Assert.NotNull(factory.Storage.Find(key)));

        factory.Storage.StopFailingDeletes(prefix);
        var finished = await RunJobAsync(user.Id);

        Assert.Equal((1, 0), (finished.AccountsDeleted, finished.StorageFailures));
        Assert.Null(await UserRowAsync(user.Id));
        Assert.All(keys, key => Assert.Null(factory.Storage.Find(key)));
    }

    [Fact]
    public async Task ThePurgeEmptiesEveryTableOfTheUserAnonymisesTheRecordsAndLeavesOthersAlone()
    {
        var (subject, email) = await NewUserAsync();
        var (bystander, _) = await NewUserAsync();
        var (third, _) = await NewUserAsync();
        using var _1 = subject;
        using var _2 = bystander;
        using var _3 = third;
        using var anonymous = factory.CreateAnonymousClient();
        var ownerId = subject.Id;

        // --- Everything a user can have, through the API where there is one, directly where setting it up would be a task of its own. ---
        var product = await factory.SeedProductAsync(anonymous, 200m, cancellationToken: Ct);

        await factory.WithDbAsync(async db =>
        {
            db.ExternalLogins.Add(new ExternalLogin { UserId = ownerId, Provider = "google", Subject = FakeExternalTokenVerifier.NewSubject(), EmailAtLink = email });
            var brandId = await db.Products.Where(row => row.Id == product.ProductId).Select(row => row.BrandId).SingleAsync(Ct);
            db.SizeEntries.Add(new SizeEntry { UserId = ownerId, BrandId = brandId, Group = SizeGroup.Tops, Size = "M", Source = SizeSource.UserEntered });
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        await subject.Client.SetCityAsync("Istanbul", Ct);
        var persona = await subject.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        await subject.Client.SwipeAsync(persona, ProfileClientExtensions.StyleCards.Take(3).Select(card => (card.Id, "Like")), Ct);
        using (var saved = await subject.Client.PutAsync($"/api/v1/me/saved/{product.ProductId}", null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        }

        await subject.Client.CreateAlertOkAsync(product.ProductId, "PriceDrop", targetPrice: 150m, cancellationToken: Ct);
        await anonymous.UpsertOkAsync(product.SellerSlug, [product.Item(120m)], Ct);
        var deviceToken = await subject.Client.RegisterDeviceAsync(cancellationToken: Ct);
        using (var preferences = await subject.Client.PutPreferencesAsync(new { priceAlerts = false }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, preferences.StatusCode);
        }

        await factory.InsertNotificationAsync(ownerId, cancellationToken: Ct);

        var itemId = await factory.AddPhotoItemAsync(subject.Client, cancellationToken: Ct);
        using (var wear = await subject.Client.WearAsync(itemId, cancellationToken: Ct))
        {
            Assert.True(wear.IsSuccessStatusCode);
        }

        var pieces = await factory.InsertWardrobeAsync(ownerId, RecommendationClientExtensions.FortyPieces, Ct);
        Assert.NotEmpty(pieces);
        var outfitId = (await subject.Client.DailyAsync("?date=tomorrow", Ct)).GetProperty("outfit").GetProperty("id").GetGuid();
        using (var savedOutfit = await subject.Client.PostOutfitAsync(outfitId, "save", cancellationToken: Ct))
        {
            Assert.True(savedOutfit.IsSuccessStatusCode);
        }

        await subject.Client.MakeCreatorAsync(ownerId, $"s{Guid.NewGuid():N}"[..12], Ct);
        await anonymous.MakeCreatorAsync(bystander.Id, $"b{Guid.NewGuid():N}"[..12], Ct);
        var subjectPost = await factory.CreateLivePostAsync(subject.Client, "Public", [SocialClientExtensions.ProductTag(product.ProductId), SocialClientExtensions.UnknownTag("Şapka")], Ct);
        var bystanderPost = await factory.CreateLivePostAsync(bystander.Client, "Public", [SocialClientExtensions.ProductTag(product.ProductId)], Ct);
        var subjectPostRow = await factory.PostRowAsync(subjectPost, Ct);
        var postKeys = new[] { subjectPostRow.ImageOriginalKey!, subjectPostRow.ImageFeedKey!, subjectPostRow.ImagePreviewKey! };
        var itemRow = await factory.ItemRowAsync(itemId, Ct);
        var itemKeys = new[] { itemRow.ImageOriginalKey!, itemRow.ImageCutoutKey!, itemRow.ImageThumbKey! };

        using (var like = await subject.Client.PostAsync($"/api/v1/posts/{bystanderPost}/like", null, Ct))
        {
            Assert.True(like.IsSuccessStatusCode);
        }

        using (var fan = await bystander.Client.PostAsync($"/api/v1/posts/{subjectPost}/like", null, Ct))
        {
            Assert.True(fan.IsSuccessStatusCode);
        }

        using (var reported = await subject.Client.ReportAsync(bystanderPost, cancellationToken: Ct))
        {
            Assert.True(reported.IsSuccessStatusCode);
        }

        await subject.Client.FollowAsync(bystander.Id, Ct);
        await bystander.Client.FollowAsync(ownerId, Ct);
        await bystander.Client.FollowAsync(third.Id, Ct);
        await subject.Client.BlockAsync(third.Id, Ct);

        // Buy clicks through the creators' posts, both ways round, and analytics events of the signed-in user.
        await ClickAsync(bystander.Client, await GoPathAsync(anonymous, subjectPost));
        await ClickAsync(subject.Client, await GoPathAsync(anonymous, bystanderPost));
        var anonymousId = AnalyticsClientExtensions.NewAnonymousId();
        await subject.Client.PostEventsOkAsync([AnalyticsClientExtensions.Event("app_open", anonymousId: anonymousId)], Ct);

        // --- Before: the subject has rows everywhere. ---
        var before = await CountsAsync(ownerId);
        Assert.All(before, entry => Assert.True(entry.Value > 0, $"{entry.Key} should have rows before the deletion."));
        Assert.Equal(1, await LikeCountAsync(bystanderPost));
        var clicksBefore = await ClicksAsync(ownerId, bystander.Id);
        Assert.Equal(2, clicksBefore.Count);
        var bystanderItems = await factory.WithDbAsync(db => db.WardrobeItems.CountAsync(row => row.UserId == bystander.Id, Ct));

        using var requested = await DeleteMeAsync(subject.Client, ApiClientExtensions.DefaultPassword);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        await BackdateAsync(ownerId, 8);

        var summary = await RunJobAsync(ownerId);

        // --- After: nothing of the subject is left, in any table. ---
        Assert.Equal(1, summary.AccountsDeleted);
        Assert.Equal(itemKeys.Length + postKeys.Length, summary.ObjectsDeleted);
        Assert.All(await CountsAsync(ownerId), entry => Assert.True(entry.Value == 0, $"{entry.Key} still has {entry.Value} row(s) of the deleted user."));
        Assert.All(itemKeys.Concat(postKeys), key => Assert.Null(factory.Storage.Find(key)));
        Assert.All(itemKeys.Concat(postKeys), key => Assert.Contains(key, factory.Storage.Deleted));

        // Anonymised, not deleted: the click and event records stay without the user, referral amounts stay without the creator.
        var clicks = await factory.WithDbAsync(db => db.Clicks.AsNoTracking().Where(click => clicksBefore.Select(before => before.Id).Contains(click.Id)).OrderBy(click => click.Id).ToListAsync(Ct));
        Assert.Equal(2, clicks.Count);
        Assert.DoesNotContain(clicks, click => click.UserId == ownerId || click.CreatorId == ownerId);
        Assert.Contains(clicks, click => click.UserId == bystander.Id && click.CreatorId is null && click.PostId == subjectPost);
        Assert.Contains(clicks, click => click.UserId is null && click.CreatorId == bystander.Id && click.PostId == bystanderPost);
        var referrals = await factory.WithDbAsync(db => db.ReferralEvents.AsNoTracking().Where(row => row.PostId == subjectPost || row.PostId == bystanderPost).ToListAsync(Ct));
        Assert.Equal(2, referrals.Count);
        Assert.Contains(referrals, row => row.PostId == subjectPost && row.CreatorId is null && row.ViewerUserId == bystander.Id && row.SharePercent == 40m);
        Assert.Contains(referrals, row => row.PostId == bystanderPost && row.CreatorId == bystander.Id && row.ViewerUserId is null && row.SharePercent == 40m);
        var events = await factory.EventsOfAnonymousIdAsync(anonymousId, Ct);
        Assert.NotEmpty(events);
        Assert.All(events, stored => Assert.Null(stored.UserId));

        // Other people: untouched. The subject's like on the bystander's post is taken back, the post and wardrobe stay.
        Assert.Equal(0, await LikeCountAsync(bystanderPost));
        Assert.Equal(HttpStatusCode.OK, await anonymous.StatusOfAsync(bystanderPost, Ct));
        Assert.Equal(bystanderItems, await factory.WithDbAsync(db => db.WardrobeItems.CountAsync(row => row.UserId == bystander.Id, Ct)));
        Assert.NotNull(await UserRowAsync(bystander.Id));
        Assert.NotNull(await UserRowAsync(third.Id));
        Assert.True(await factory.WithDbAsync(db => db.Follows.AnyAsync(follow => follow.FollowerId == bystander.Id && follow.FolloweeId == third.Id, Ct)));
        Assert.True(await factory.WithDbAsync(db => db.CreatorProfiles.AnyAsync(creator => creator.UserId == bystander.Id, Ct)));
        Assert.True(await factory.WithDbAsync(db => db.StyleCards.AnyAsync(Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await anonymous.StatusOfAsync(subjectPost, Ct));
        Assert.Empty(await factory.DevicesWithTokenAsync(deviceToken, Ct));
    }

    [Fact]
    public async Task TheAdminPanelShowsPendingDeletionAndCanDeleteNowOrCancel()
    {
        var (pendingUser, pendingEmail) = await NewUserAsync();
        var (otherUser, otherEmail) = await NewUserAsync();
        using var _1 = pendingUser;
        using var _2 = otherUser;
        using var anonymous = factory.CreateAnonymousClient();
        using var browser = await factory.SignedInAdminAsync(Ct);
        var itemId = await factory.AddPhotoItemAsync(pendingUser.Client, cancellationToken: Ct);
        var originalKey = await factory.OriginalKeyAsync(itemId, Ct);
        using var requested = await DeleteMeAsync(pendingUser.Client, ApiClientExtensions.DefaultPassword);
        using var otherRequested = await DeleteMeAsync(otherUser.Client, ApiClientExtensions.DefaultPassword);

        var page = await browser.GetHtmlAsync($"/admin/users?id={pendingUser.Id}", Ct);
        Assert.Contains("Pending deletion", page, StringComparison.Ordinal);
        Assert.Contains("Delete now", page, StringComparison.Ordinal);
        Assert.Contains("Cancel deletion", page, StringComparison.Ordinal);

        // Cancel: the user signs in again, the audit row is written.
        using var cancel = await browser.PostFormAsync($"/admin/users?id={otherUser.Id}", "/admin/users?handler=CancelDeletion", [new("id", otherUser.Id.ToString())], Ct);
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Assert.Null((await UserRowAsync(otherUser.Id))!.DeletionRequestedAt);
        Assert.Single(await factory.AuditOfAsync("user.cancel-deletion", otherUser.Id.ToString(), Ct));
        using var login = await anonymous.LoginAsync(otherEmail, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Delete now: gone at once, objects included, audited without anything personal.
        using var delete = await browser.PostFormAsync($"/admin/users?id={pendingUser.Id}", "/admin/users?handler=DeleteNow", [new("id", pendingUser.Id.ToString())], Ct);
        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.Null(await UserRowAsync(pendingUser.Id));
        Assert.Null(factory.Storage.Find(originalKey));
        var audit = Assert.Single(await factory.AuditOfAsync("user.delete", pendingUser.Id.ToString(), Ct));
        Assert.DoesNotContain(pendingEmail, audit.Summary ?? string.Empty, StringComparison.Ordinal);

        using var again = await browser.PostFormAsync($"/admin/users?id={pendingUser.Id}", "/admin/users?handler=DeleteNow", [new("id", pendingUser.Id.ToString())], Ct);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Single(await factory.AuditOfAsync("user.delete", pendingUser.Id.ToString(), Ct));
    }

    [Fact]
    public async Task AccountDeletionEndpointsAreForSignedInCallersOnly()
    {
        using var anonymous = factory.CreateAnonymousClient();

        using var response = await DeleteMeAsync(anonymous, ApiClientExtensions.DefaultPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // helpers for the matrix

    private async Task<int> LikeCountAsync(Guid postId) => (await factory.PostRowAsync(postId, Ct)).LikeCount;

    private static async Task<string> GoPathAsync(HttpClient client, Guid postId)
    {
        var post = await client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        var tag = post.GetProperty("tags").EnumerateArray().First(candidate => candidate.GetProperty("kind").GetString() == "Product");

        return tag.GetProperty("offer").GetProperty("goPath").GetString()!;
    }

    private static async Task ClickAsync(HttpClient client, string goPath)
    {
        using var response = await client.GetAsync(goPath, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private Task<List<Kombin.Api.Persistence.Entities.Engagement.Click>> ClicksAsync(Guid first, Guid second) =>
        factory.WithDbAsync(db => db.Clicks.AsNoTracking()
            .Where(click => click.UserId == first || click.UserId == second || click.CreatorId == first || click.CreatorId == second)
            .Where(click => click.PostId != null)
            .ToListAsync(Ct));

    /// <summary>How many rows of every user-owned table the user has: all must be 0 after the purge.</summary>
    private Task<Dictionary<string, int>> CountsAsync(Guid userId) => factory.WithDbAsync(async db => new Dictionary<string, int>
    {
        ["identity.users"] = await db.Users.CountAsync(row => row.Id == userId, Ct),
        ["identity.external_logins"] = await db.ExternalLogins.CountAsync(row => row.UserId == userId, Ct),
        ["identity.refresh_tokens"] = await db.RefreshTokens.CountAsync(row => row.UserId == userId, Ct),
        ["profile.user_profiles"] = await db.UserProfiles.CountAsync(row => row.UserId == userId, Ct),
        ["profile.personas"] = await db.Personas.CountAsync(row => row.UserId == userId, Ct),
        ["profile.persona_style_profiles"] = await db.PersonaStyleProfiles.CountAsync(row => db.Personas.Any(persona => persona.Id == row.PersonaId && persona.UserId == userId), Ct),
        ["profile.style_swipes"] = await db.StyleSwipes.CountAsync(row => db.Personas.Any(persona => persona.Id == row.PersonaId && persona.UserId == userId), Ct),
        ["profile.size_entries"] = await db.SizeEntries.CountAsync(row => row.UserId == userId, Ct),
        ["engagement.saved_products"] = await db.SavedProducts.CountAsync(row => row.UserId == userId, Ct),
        ["engagement.price_alerts"] = await db.PriceAlerts.CountAsync(row => row.UserId == userId, Ct),
        ["engagement.alert_triggers"] = await db.AlertTriggers.CountAsync(row => row.UserId == userId, Ct),
        ["notification.notifications"] = await db.Notifications.CountAsync(row => row.UserId == userId, Ct),
        ["notification.devices"] = await db.Devices.CountAsync(row => row.UserId == userId, Ct),
        ["notification.notification_preferences"] = await db.NotificationPreferences.CountAsync(row => row.UserId == userId, Ct),
        ["wardrobe.items"] = await db.WardrobeItems.CountAsync(row => row.UserId == userId, Ct),
        ["wardrobe.wears"] = await db.WardrobeWears.CountAsync(row => row.UserId == userId, Ct),
        ["recommendation.outfits"] = await db.Outfits.CountAsync(row => row.UserId == userId, Ct),
        ["recommendation.outfit_items"] = await db.OutfitItems.CountAsync(row => db.Outfits.Any(outfit => outfit.Id == row.OutfitId && outfit.UserId == userId), Ct),
        ["recommendation.outfit_feedback"] = await db.OutfitFeedback.CountAsync(row => row.UserId == userId, Ct),
        ["social.posts"] = await db.Posts.CountAsync(row => row.AuthorId == userId, Ct),
        ["social.post_tags"] = await db.PostTags.CountAsync(row => db.Posts.Any(post => post.Id == row.PostId && post.AuthorId == userId), Ct),
        ["social.likes"] = await db.PostLikes.CountAsync(row => row.UserId == userId || db.Posts.Any(post => post.Id == row.PostId && post.AuthorId == userId), Ct),
        ["social.reports"] = await db.PostReports.CountAsync(row => row.ReporterId == userId, Ct),
        ["social.follows"] = await db.Follows.CountAsync(row => row.FollowerId == userId || row.FolloweeId == userId, Ct),
        ["social.blocks"] = await db.UserBlocks.CountAsync(row => row.BlockerId == userId || row.BlockedId == userId, Ct),
        ["social.posting_consents"] = await db.PostingConsents.CountAsync(row => row.UserId == userId, Ct),
        ["social.creator_profiles"] = await db.CreatorProfiles.CountAsync(row => row.UserId == userId, Ct),
    });
}
