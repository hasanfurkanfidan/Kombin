using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Identity;
using Kombin.Api.Features.Identity.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Notification;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.TryOn;
using Kombin.Api.Persistence.Entities.Wardrobe;
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

    /// <summary>The league tables a player who entered, voted and was reported has rows in.</summary>
    private static readonly string[] PlayedLeagueTables = ["league.players", "league.consents", "league.entries", "league.entry_pieces", "league.pairs", "league.reports", "league.voter_days", "league.group_members"];

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
            setters => setters.SetProperty(user => user.DeletionRequestedAt, TestClock.UtcNow.AddDays(-days)), Ct));

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
        Assert.InRange(TestClock.UtcNow - requestedAt, TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(30));
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
    public async Task ADatabaseFailureOnOneAccountDoesNotStopTheOthersOrTheRun()
    {
        var (broken, _) = await NewUserAsync();
        var (fine, _) = await NewUserAsync();
        using var _1 = broken;
        using var _2 = fine;
        foreach (var user in new[] { broken, fine })
        {
            using var requested = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword);
            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        }

        // The broken account asked first, so it is the first of the batch; its row cannot be deleted (a trigger on a marker name stands in for any
        // database failure: the purge's transaction fails after the objects are already gone).
        await BackdateAsync(broken.Id, 10);
        await BackdateAsync(fine.Id, 8);
        await factory.WithDbAsync(db => db.Users.Where(user => user.Id == broken.Id).ExecuteUpdateAsync(set => set.SetProperty(user => user.DisplayName, "b6-simulated-failure"), Ct));
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"CREATE FUNCTION b6_fail_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF OLD.display_name = 'b6-simulated-failure' THEN RAISE EXCEPTION 'simulated failure'; END IF; RETURN OLD; END $$; CREATE TRIGGER b6_fail_delete BEFORE DELETE ON identity.users FOR EACH ROW EXECUTE FUNCTION b6_fail_delete();",
            Ct));

        try
        {
            var summary = await RunJobAsync(broken.Id, fine.Id);

            // The healthy account is gone, the broken one is untouched and counted; the run finished.
            Assert.Equal((1, 1), (summary.AccountsDeleted, summary.Failures));
            Assert.Null(await UserRowAsync(fine.Id));
            Assert.NotNull(await UserRowAsync(broken.Id));
        }
        finally
        {
            await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"DROP TRIGGER b6_fail_delete ON identity.users; DROP FUNCTION b6_fail_delete();", Ct));
        }

        // The next run (the database is fine again) finishes the broken one.
        var next = await RunJobAsync(broken.Id);
        Assert.Equal((1, 0), (next.AccountsDeleted, next.Failures));
        Assert.Null(await UserRowAsync(broken.Id));
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
        Assert.Equal((0, 1), (failed.AccountsDeleted, failed.Failures));
        Assert.NotNull(await UserRowAsync(user.Id));
        Assert.Equal(1, await factory.WithDbAsync(db => db.WardrobeItems.CountAsync(row => row.UserId == user.Id, Ct)));
        Assert.All(keys, key => Assert.NotNull(factory.Storage.Find(key)));

        factory.Storage.StopFailingDeletes(prefix);
        var finished = await RunJobAsync(user.Id);

        Assert.Equal((1, 0), (finished.AccountsDeleted, finished.Failures));
        Assert.Null(await UserRowAsync(user.Id));
        Assert.All(keys, key => Assert.Null(factory.Storage.Find(key)));
    }

    [Fact]
    public async Task BulkAddObjectsAreFoundByTheirPrefixEvenWithoutARowAndAFailureToDeleteThemPostponesTheDeletion()
    {
        var (user, _) = await NewUserAsync();
        using var _1 = user;
        var keys = await SeedBulkAsync(user.Id);

        // An object whose row is gone (a crashed delete): only the prefix finds it.
        var orphan = StorageKeys.BuildBulk(user.Id, Guid.NewGuid(), Guid.NewGuid(), ImageVariant.Staged, "png");
        factory.Storage.Upload(orphan, [9], "image/png");
        using var requested = await DeleteMeAsync(user.Client, ApiClientExtensions.DefaultPassword);
        await BackdateAsync(user.Id, 8);

        var prefix = StorageKeys.BulkUserPrefix(user.Id);
        factory.Storage.FailDeletesUnder(prefix);
        var failed = await RunJobAsync(user.Id);

        // Nothing was touched: the account, the batch and every object are there for the retry.
        Assert.Equal((0, 1), (failed.AccountsDeleted, failed.Failures));
        Assert.NotNull(await UserRowAsync(user.Id));
        Assert.Equal(1, await factory.WithDbAsync(db => db.BulkBatches.CountAsync(row => row.UserId == user.Id, Ct)));
        Assert.All(keys.Append(orphan), key => Assert.NotNull(factory.Storage.Find(key)));

        factory.Storage.StopFailingDeletes(prefix);
        var finished = await RunJobAsync(user.Id);

        Assert.Equal((1, 0), (finished.AccountsDeleted, finished.Failures));
        Assert.Equal(keys.Length + 1, finished.ObjectsDeleted);
        Assert.Null(await UserRowAsync(user.Id));
        Assert.All(keys.Append(orphan), key => Assert.Null(factory.Storage.Find(key)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.BulkBatches.CountAsync(row => row.UserId == user.Id, Ct)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.BulkPieces.CountAsync(row => row.UserId == user.Id, Ct)));
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

        var savedTrip = await subject.Client.SaveTripAsync("Gezi", TripClientExtensions.TripRequest("Istanbul", days: 2), Ct);
        Assert.Equal(HttpStatusCode.Created, savedTrip.Status);

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

        // The subject's click earned a size question five days later ("Aldın mı? Beden uydu mu?").
        await factory.WithDbAsync(db => db.Clicks.Where(click => click.UserId == ownerId).ExecuteUpdateAsync(set => set.SetProperty(click => click.ClickedAt, TestClock.UtcNow.AddDays(-5)), Ct));
        Assert.Single((await subject.Client.GetJsonAsync("/api/v1/me/size-prompts", Ct)).GetProperty("prompts").EnumerateArray());
        var anonymousId = AnalyticsClientExtensions.NewAnonymousId();
        await subject.Client.PostEventsOkAsync([AnalyticsClientExtensions.Event("app_open", anonymousId: anonymousId)], Ct);

        // Kombin Ligi: the subject joined (through the API) and played a day in 2020 against the bystander (rows set up directly: no job yet).
        await subject.Client.JoinLeagueOkAsync(Ct);
        var league = await SeedLeagueDayAsync(ownerId, bystander.Id);

        // "Manken üstünde gör": a prepared garment of the subject's photo item, a drawing (it reveals body proportions) and a report, set up directly.
        var tryOnKeys = await SeedTryOnAsync(ownerId, itemId, outfitId);

        // "Toplu ekle": a batch with two staged pieces, the room photo and four staged images, and a bystander's batch whose objects must stay.
        var bulkKeys = await SeedBulkAsync(ownerId);
        var bystanderBulkKeys = await SeedBulkAsync(bystander.Id);

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
        Assert.Equal(itemKeys.Length + postKeys.Length + 1 + tryOnKeys.Length + bulkKeys.Length, summary.ObjectsDeleted);
        Assert.All(await CountsAsync(ownerId), entry => Assert.True(entry.Value == 0, $"{entry.Key} still has {entry.Value} row(s) of the deleted user."));
        Assert.All(itemKeys.Concat(postKeys).Append(league.ImageKey).Concat(tryOnKeys).Concat(bulkKeys), key => Assert.Null(factory.Storage.Find(key)));
        Assert.DoesNotContain(factory.Storage.KeysOfUser(ownerId), key => key.StartsWith("bulk/", StringComparison.Ordinal));
        Assert.All(bystanderBulkKeys, key => Assert.NotNull(factory.Storage.Find(key)));
        Assert.All(itemKeys.Concat(postKeys).Append(league.ImageKey).Concat(tryOnKeys).Concat(bulkKeys), key => Assert.Contains(key, factory.Storage.Deleted));

        // The league of the others: the bystander's closed day and entry stay as they were; the engine's preference row has no user to lose.
        var bystanderScore = await factory.WithDbAsync(db => db.LeagueDailyScores.AsNoTracking().SingleAsync(row => row.UserId == bystander.Id && row.Day == league.Day, Ct));
        Assert.Equal((league.BystanderTotal, league.BystanderEntryId), (bystanderScore.Total, bystanderScore.EntryId));
        Assert.True(await factory.WithDbAsync(db => db.LeagueVoterDays.AnyAsync(row => row.UserId == bystander.Id && row.Day == league.Day, Ct)));
        Assert.True(await factory.WithDbAsync(db => db.LeagueEntries.AnyAsync(row => row.Id == league.BystanderEntryId, Ct)));
        Assert.True(await factory.WithDbAsync(db => db.LeagueEnginePreferences.AnyAsync(row => row.Id == league.EnginePreferenceId, Ct)));

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

    [Fact]
    public async Task ALeaguePlayersEntryCopiesVotesAndReportsGoWithTheAccount()
    {
        // Kombin Ligi through the real flows: the subject's worn outfit entered (the job copied its thumbnails), the subject voted, the
        // bystander was shown the subject's outfit and reported it.
        var day = LeagueClientExtensions.NewDay();
        using var subject = await factory.CreatePlayerAsync(day, Ct);
        using var bystander = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(subject.Id, Ct);
        subject.Client.At(LeagueClientExtensions.Time(day, 9));
        await subject.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await factory.RunLeagueEntriesAsync(LeagueClientExtensions.Time(day, 9, 1), [subject.Id], Ct);
        var subjectEntry = (await factory.EntryAsync(subject.Id, day, Ct))!;
        var copies = subjectEntry.Pieces.Select(piece => piece.ImageKey!).ToList();
        Assert.Equal(3, copies.Count);
        Assert.All(copies, key => Assert.NotNull(factory.Storage.Find(key)));

        var bystanderEntry = await factory.InsertEntryAsync(day, bystander.Id, exposures: 10, cancellationToken: Ct);
        await factory.InsertEngineEntriesAsync(day, 2, Ct);

        subject.Client.At(LeagueClientExtensions.Time(day, 12));
        await subject.Client.VoteOkAsync(await subject.Client.ServeAndLookAsync(Ct), "left", cancellationToken: Ct);
        bystander.Client.At(LeagueClientExtensions.Time(day, 12));
        var shown = await bystander.Client.ServeAndLookAsync(Ct);
        var shownRow = await factory.WithDbAsync(db => db.LeaguePairs.AsNoTracking().SingleAsync(pair => pair.Id == shown, Ct));
        Assert.Contains(subjectEntry.Id, new[] { shownRow.LeftEntryId, shownRow.RightEntryId });
        using (var reported = await bystander.Client.ReportOutfitAsync(shown, shownRow.LeftEntryId == subjectEntry.Id ? "left" : "right", cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);
        }

        var before = await CountsAsync(subject.Id);
        Assert.All(PlayedLeagueTables, table => Assert.True(before[table] > 0, table));

        subject.Client.DefaultRequestHeaders.Remove(TestHeaderClock.Header);
        using (var requested = await DeleteMeAsync(subject.Client, ApiClientExtensions.DefaultPassword))
        {
            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        }

        await BackdateAsync(subject.Id, 8);
        Assert.Equal(1, (await RunJobAsync(subject.Id)).AccountsDeleted);

        Assert.All((await CountsAsync(subject.Id)).Where(entry => entry.Key.StartsWith("league.", StringComparison.Ordinal)), entry => Assert.True(entry.Value == 0, $"{entry.Key} still has {entry.Value} row(s)."));
        Assert.All(copies, key => Assert.Null(factory.Storage.Find(key)));
        Assert.All(copies, key => Assert.Contains(key, factory.Storage.Deleted));

        // The bystander keeps their own entry; the pair that showed the subject's outfit went with it, and so did the report on it.
        Assert.True(await factory.WithDbAsync(db => db.LeagueEntries.AnyAsync(entry => entry.Id == bystanderEntry, Ct)));
        Assert.False(await factory.WithDbAsync(db => db.LeaguePairs.AnyAsync(pair => pair.Id == shown, Ct)));
        Assert.False(await factory.WithDbAsync(db => db.LeagueReports.AnyAsync(report => report.ReporterId == bystander.Id, Ct)));
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
        ["profile.size_prompts"] = await db.SizePrompts.CountAsync(row => row.UserId == userId, Ct),
        ["engagement.saved_products"] = await db.SavedProducts.CountAsync(row => row.UserId == userId, Ct),
        ["engagement.price_alerts"] = await db.PriceAlerts.CountAsync(row => row.UserId == userId, Ct),
        ["engagement.alert_triggers"] = await db.AlertTriggers.CountAsync(row => row.UserId == userId, Ct),
        ["notification.notifications"] = await db.Notifications.CountAsync(row => row.UserId == userId, Ct),
        ["notification.devices"] = await db.Devices.CountAsync(row => row.UserId == userId, Ct),
        ["notification.notification_preferences"] = await db.NotificationPreferences.CountAsync(row => row.UserId == userId, Ct),
        ["wardrobe.items"] = await db.WardrobeItems.CountAsync(row => row.UserId == userId, Ct),
        ["wardrobe.wears"] = await db.WardrobeWears.CountAsync(row => row.UserId == userId, Ct),
        ["wardrobe.bulk_batches"] = await db.BulkBatches.CountAsync(row => row.UserId == userId, Ct),
        ["wardrobe.bulk_pieces"] = await db.BulkPieces.CountAsync(row => row.UserId == userId, Ct),
        ["recommendation.outfits"] = await db.Outfits.CountAsync(row => row.UserId == userId, Ct),
        ["recommendation.outfit_items"] = await db.OutfitItems.CountAsync(row => db.Outfits.Any(outfit => outfit.Id == row.OutfitId && outfit.UserId == userId), Ct),
        ["recommendation.outfit_feedback"] = await db.OutfitFeedback.CountAsync(row => row.UserId == userId, Ct),
        ["recommendation.trips"] = await db.Trips.CountAsync(row => row.UserId == userId, Ct),
        ["social.posts"] = await db.Posts.CountAsync(row => row.AuthorId == userId, Ct),
        ["social.post_tags"] = await db.PostTags.CountAsync(row => db.Posts.Any(post => post.Id == row.PostId && post.AuthorId == userId), Ct),
        ["social.likes"] = await db.PostLikes.CountAsync(row => row.UserId == userId || db.Posts.Any(post => post.Id == row.PostId && post.AuthorId == userId), Ct),
        ["social.reports"] = await db.PostReports.CountAsync(row => row.ReporterId == userId, Ct),
        ["social.follows"] = await db.Follows.CountAsync(row => row.FollowerId == userId || row.FolloweeId == userId, Ct),
        ["social.blocks"] = await db.UserBlocks.CountAsync(row => row.BlockerId == userId || row.BlockedId == userId, Ct),
        ["social.posting_consents"] = await db.PostingConsents.CountAsync(row => row.UserId == userId, Ct),
        ["social.creator_profiles"] = await db.CreatorProfiles.CountAsync(row => row.UserId == userId, Ct),
        ["league.players"] = await db.LeaguePlayers.CountAsync(row => row.UserId == userId, Ct),
        ["league.consents"] = await db.LeagueConsents.CountAsync(row => row.UserId == userId, Ct),
        ["league.entries"] = await db.LeagueEntries.CountAsync(row => row.UserId == userId, Ct),
        ["league.entry_pieces"] = await db.LeagueEntryPieces.CountAsync(row => db.LeagueEntries.Any(entry => entry.Id == row.EntryId && entry.UserId == userId), Ct),
        ["league.pairs"] = await db.LeaguePairs.CountAsync(row => row.VoterId == userId || db.LeagueEntries.Any(entry => (entry.Id == row.LeftEntryId || entry.Id == row.RightEntryId) && entry.UserId == userId), Ct),
        ["league.reports"] = await db.LeagueReports.CountAsync(row => row.ReporterId == userId || db.LeagueEntries.Any(entry => entry.Id == row.EntryId && entry.UserId == userId), Ct),
        ["league.daily_scores"] = await db.LeagueDailyScores.CountAsync(row => row.UserId == userId, Ct),
        ["league.voter_days"] = await db.LeagueVoterDays.CountAsync(row => row.UserId == userId, Ct),
        ["league.group_members"] = await db.LeagueGroupMembers.CountAsync(row => row.UserId == userId, Ct),
        ["league.style_scores"] = await db.LeagueStyleScores.CountAsync(row => row.UserId == userId, Ct),
        ["tryon.garments"] = await db.TryOnGarments.CountAsync(row => db.WardrobeItems.Any(item => item.Id == row.WardrobeItemId && item.UserId == userId), Ct),
        ["tryon.renders"] = await db.TryOnRenders.CountAsync(row => row.UserId == userId, Ct),
        ["tryon.reports"] = await db.TryOnReports.CountAsync(row => row.UserId == userId, Ct),
    });

    /// <summary>
    /// The subject's bulk-add batch as rows and objects: the room photo (a <c>Failed</c> batch keeps it) and, for two staged pieces, the cut-out and the
    /// thumbnail under their private names. The keys are found by the prefix <c>bulk/{user}/</c>, not through the rows. Returns the object keys.
    /// </summary>
    private Task<string[]> SeedBulkAsync(Guid userId) => factory.WithDbAsync(async db =>
    {
        var batch = new BulkBatch { UserId = userId, Status = BulkBatchStatus.Ready, ExpiresAt = TestClock.UtcNow.AddHours(24) };
        var photo = StorageKeys.BuildBulk(userId, batch.Id, batch.Id, ImageVariant.Original, "jpg");
        batch.OriginalKey = photo;
        var pieces = new[]
        {
            new BulkPiece { BatchId = batch.Id, UserId = userId, SortOrder = 0, PreTicked = true },
            new BulkPiece { BatchId = batch.Id, UserId = userId, SortOrder = 1, Reasons = ["touching"] },
        };
        List<string> keys = [photo];
        foreach (var piece in pieces)
        {
            piece.CutoutKey = StorageKeys.BuildBulk(userId, batch.Id, piece.Id, ImageVariant.Staged, "png");
            piece.ThumbKey = StorageKeys.BuildBulk(userId, batch.Id, piece.Id, ImageVariant.StagedThumb, "webp");
            keys.AddRange([piece.CutoutKey, piece.ThumbKey]);
        }

        foreach (var key in keys)
        {
            factory.Storage.Upload(key, [1, 2, 3], key.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
        }

        db.BulkBatches.Add(batch);
        db.BulkPieces.AddRange(pieces);
        await db.SaveChangesAsync(Ct);

        return keys.ToArray();
    });

    /// <summary>The subject's try-on rows and their two stored objects (the prepared cutout and the drawing); returns the object keys.</summary>
    private Task<string[]> SeedTryOnAsync(Guid userId, Guid itemId, Guid outfitId) => factory.WithDbAsync(async db =>
    {
        var now = TestClock.UtcNow;
        var garment = new TryOnGarment
        {
            WardrobeItemId = itemId,
            Status = TryOnGarmentStatus.Prepared,
            SourceHash = new string('a', TryOnGarment.HashLength),
            PreparedHash = new string('a', TryOnGarment.HashLength),
            PrepareVersion = "prepare-v6",
            Family = "top",
            Source = "cutout",
            Reliable = true,
            LandmarksJson = "{}",
            PreparedAt = now,
        };
        garment.CutoutKey = StorageKeys.Build("tryon", userId, garment.Id, ImageVariant.Prepared, "png");
        var render = new TryOnRender
        {
            UserId = userId,
            CacheKey = new string('c', TryOnRender.HashLength),
            BodyHash = new string('b', TryOnRender.HashLength),
            Renderer = "mannequin-v5",
            Width = 600,
            Height = 1200,
            CreatedAt = now,
            LastUsedAt = now,
        };
        render.ImageKey = StorageKeys.Build("tryon", userId, render.Id, ImageVariant.Render, "webp");
        factory.Storage.Upload(garment.CutoutKey, [1, 2, 3], "image/png");
        factory.Storage.Upload(render.ImageKey, [4, 5, 6], "image/webp");

        db.TryOnGarments.Add(garment);
        db.TryOnRenders.Add(render);
        db.TryOnReports.Add(new TryOnReport { UserId = userId, OutfitId = outfitId, GarmentIds = [garment.Id], Renderer = "mannequin-v5", CreatedAt = now });
        await db.SaveChangesAsync(Ct);

        return new[] { garment.CutoutKey, render.ImageKey };
    });

    private sealed record LeagueSeed(DateOnly Day, string ImageKey, Guid BystanderEntryId, int BystanderTotal, long EnginePreferenceId);

    /// <summary>
    /// A closed league day in 2020 (no other test's data is there) where the subject and the bystander each had an entry, voted on a pair
    /// showing the other's outfit, reported it, scored, and sat in one group; plus the subject's style score and one engine preference row.
    /// The subject's entry has a league image copy in storage.
    /// </summary>
    private async Task<LeagueSeed> SeedLeagueDayAsync(Guid subjectId, Guid bystanderId)
    {
        var day = new DateOnly(2020, 1, 6).AddDays(Random.Shared.Next(0, 52) * 7);
        var imageKey = $"league/{Guid.NewGuid():D}/{Guid.NewGuid():D}/thumb.webp";
        await factory.Storage.PutAsync(imageKey, [1, 2, 3], "image/webp", Ct);

        LeagueEntry Entry(Guid userId) => new()
        {
            Day = day,
            UserId = userId,
            Kind = LeagueEntryKind.Human,
            Audience = Audience.Women,
            PieceIds = [Guid.NewGuid(), Guid.NewGuid()],
            PieceSetHash = new string('a', LeagueEntry.PieceSetHashLength),
            Status = LeagueEntryStatus.Competing,
            Comparisons = 1,
            WeightedWins = 1,
            WeightedGames = 1,
        };

        var mine = Entry(subjectId);
        mine.Pieces.Add(new LeagueEntryPiece { Position = 0, Slot = OutfitSlot.Top, ImageKey = imageKey });
        var theirs = Entry(bystanderId);
        theirs.Pieces.Add(new LeagueEntryPiece { Position = 0, Slot = OutfitSlot.Top, ImageUrl = "https://images.example.test/top.jpg" });
        var (low, high) = mine.Id.CompareTo(theirs.Id) < 0 ? (mine.Id, theirs.Id) : (theirs.Id, mine.Id);
        var served = TestClock.UtcNow.AddMinutes(-5);
        var group = new LeagueGroup { WeekStart = day, Tiers = [LeagueTier.Bronze], Capacity = 30 };
        var preference = new LeagueEnginePreference { Day = day, Audience = Audience.Women, WinnerProducts = [Guid.NewGuid()], Weight = 1 };

        LeaguePair Pair(Guid voterId, bool pickLeft) => new()
        {
            Day = day,
            VoterId = voterId,
            LeftEntryId = mine.Id,
            RightEntryId = theirs.Id,
            LowEntryId = low,
            HighEntryId = high,
            MatchLevel = LeaguePairMatch.Strict,
            Sequence = 1,
            ServedAt = served,
            AnsweredAt = served.AddSeconds(3),
            Choice = pickLeft ? LeaguePairChoice.Left : LeaguePairChoice.Right,
            Counted = true,
            Weight = 1,
        };

        await factory.WithDbAsync(async db =>
        {
            db.LeagueEntries.AddRange(mine, theirs);
            db.LeaguePairs.AddRange(Pair(subjectId, pickLeft: false), Pair(bystanderId, pickLeft: true));
            db.LeagueReports.AddRange(
                new LeagueReport { EntryId = theirs.Id, ReporterId = subjectId, CreatedAt = served },
                new LeagueReport { EntryId = mine.Id, ReporterId = bystanderId, CreatedAt = served });
            db.LeagueDailyScores.AddRange(
                new LeagueDailyScore { UserId = subjectId, Day = day, EntryId = mine.Id, Competed = true, VotingPoints = 15, Total = 15 },
                new LeagueDailyScore { UserId = bystanderId, Day = day, EntryId = theirs.Id, Competed = true, WornPoints = 10, Total = 10 });
            db.LeagueVoterDays.AddRange(
                new LeagueVoterDay { UserId = subjectId, Day = day, Served = 1, Counted = 1 },
                new LeagueVoterDay { UserId = bystanderId, Day = day, Served = 1, Counted = 1 });
            if (!await db.LeagueWeeks.AnyAsync(week => week.WeekStart == day, Ct))
            {
                db.LeagueWeeks.Add(new LeagueWeek { WeekStart = day, EndsAt = served });
            }

            db.LeagueGroups.Add(group);
            db.LeagueGroupMembers.AddRange(
                new LeagueGroupMember { WeekStart = day, UserId = subjectId, GroupId = group.Id, Alias = "Yeşil Trençkot", Tier = LeagueTier.Bronze, JoinedAt = served },
                new LeagueGroupMember { WeekStart = day, UserId = bystanderId, GroupId = group.Id, Alias = "Lacivert Blazer", Tier = LeagueTier.Bronze, JoinedAt = served });
            db.LeagueStyleScores.Add(new LeagueStyleScore { UserId = subjectId, ComputedAt = served, CountedDays = 1 });
            db.LeagueEnginePreferences.Add(preference);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        return new LeagueSeed(day, imageKey, theirs.Id, 10, preference.Id);
    }
}
