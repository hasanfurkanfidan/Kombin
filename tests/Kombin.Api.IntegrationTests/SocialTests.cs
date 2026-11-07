using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kombin.Api.Features.Social;
using Kombin.Api.Features.Social.Jobs;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>The social feature end to end: consent, the post pipeline, visibility, the feed, reports, deletion, creators and attribution.</summary>
public sealed class SocialTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostingNeedsTheCurrentConsentAndStoresWhatWasShown()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var refused = await user.Client.CreatePostUploadUrlRawAsync(cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Social.ConsentRequired", (await refused.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        var before = await user.Client.GetJsonAsync("/api/v1/social/consent", Ct);
        Assert.Equal(PostingConsent.Current.Version, before.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("acceptedAt").ValueKind);

        using var outdated = await user.Client.PostAsJsonAsync("/api/v1/social/consent", new { version = "2020-01-v0" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, outdated.StatusCode);
        Assert.Equal("Social.ConsentOutdated", (await outdated.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        await user.Client.AcceptConsentAsync(Ct);
        await user.Client.AcceptConsentAsync(Ct);

        var records = await factory.WithDbAsync(db => db.PostingConsents.AsNoTracking().Where(consent => consent.UserId == user.Id).ToListAsync(Ct));
        var record = Assert.Single(records);
        Assert.Equal(PostingConsent.Current.Version, record.Version);
        Assert.Equal(PostingConsent.Current.Text, record.Text);

        using var allowed = await user.Client.CreatePostUploadUrlRawAsync(cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        var postId = (await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("postId").GetGuid();
        var post = await factory.PostRowAsync(postId, Ct);
        Assert.Equal((PostingConsent.Current.Version, record.AcceptedAt), (post.ConsentVersion, post.ConsentAt));
        Assert.Equal((PostStatus.Processing, PostVisibility.Private), (post.Status, post.Visibility));

        using var heic = await user.Client.CreatePostUploadUrlRawAsync("image/heic", cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, heic.StatusCode);
    }

    [Fact]
    public async Task APhotoIsStrippedModeratedAndProposedThenPublishedWithConfirmedTagsOnly()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var itemId = await factory.InsertItemAsync(author.Id, "ust-giyim/tisort", ProductColor.Red, cancellationToken: Ct);
        var productId = await anonymous.SeedProductAsync("alt-giyim/jean", "Blue", 2, price: 899m, cancellationToken: Ct);

        var postId = await factory.CreateProcessedPostAsync(author.Client, cancellationToken: Ct);

        // Metadata is gone from the stored original (GPS, comment), the orientation survives, the pixels are untouched.
        var row = await factory.PostRowAsync(postId, Ct);
        Assert.Equal(PostStatus.Draft, row.Status);
        var original = Encoding.Latin1.GetString(factory.Storage.Find(row.ImageOriginalKey!)!.Content);
        Assert.DoesNotContain(FakeVisionClient.GpsMarker, original, StringComparison.Ordinal);
        Assert.DoesNotContain("taken at home", original, StringComparison.Ordinal);
        Assert.Contains("photo:ok:", original, StringComparison.Ordinal);
        Assert.Equal(6, ImageMetadataOrientation(factory.Storage.Find(row.ImageOriginalKey!)!.Content));
        Assert.Equal(FakeVisionClient.FeedImage, factory.Storage.Find(row.ImageFeedKey!)!.Content);
        Assert.Equal(FakeVisionClient.Preview, factory.Storage.Find(row.ImagePreviewKey!)!.Content);
        Assert.Equal(0.75, row.ImageAspect);

        // The author sees the proposals in the editor; nobody sees the draft.
        var draft = await author.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal(0, draft.GetProperty("tags").GetArrayLength());
        var proposals = draft.GetProperty("proposals").EnumerateArray().ToList();
        Assert.Equal(["ust-giyim/tisort", "alt-giyim/jean"], proposals.Select(proposal => proposal.GetProperty("categorySlug").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, await anonymous.StatusOfAsync(postId, Ct));

        var published = await author.Client.PublishAsync(postId, "Public", [SocialClientExtensions.WardrobeTag(itemId), SocialClientExtensions.ProductTag(productId)], "Cuma kombini", Ct);
        Assert.Equal("Live", published.GetProperty("status").GetString());
        Assert.False(published.TryGetProperty("proposals", out _));

        // Viewers get exactly the confirmed tags, the product with its cheapest offer behind /go, never a seller URL.
        var seen = await anonymous.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.False(seen.TryGetProperty("proposals", out _));
        Assert.False(seen.TryGetProperty("statusMessage", out _));
        var tags = seen.GetProperty("tags").EnumerateArray().ToList();
        Assert.Equal(["Wardrobe", "Product"], tags.Select(tag => tag.GetProperty("kind").GetString()));
        var offer = tags[1].GetProperty("offer");
        Assert.Equal(899m, offer.GetProperty("price").GetDecimal());
        Assert.Equal($"/api/v1/go/{offer.GetProperty("offerId").GetGuid()}?src=feed&post={postId}", offer.GetProperty("goPath").GetString());
        Assert.DoesNotContain("shop.example.com", seen.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(899m, seen.GetProperty("summary").GetProperty("totalPrice").GetDecimal());
        Assert.Equal(2, seen.GetProperty("summary").GetProperty("pieceCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("summary").GetProperty("ownedCount").ValueKind);
        Assert.Contains("X-Amz-Signature", seen.GetProperty("image").GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal("Cuma kombini", seen.GetProperty("caption").GetString());

        // A viewer who owns a red t-shirt has the wardrobe piece "similar"; the author owns it exactly.
        using var viewer = await factory.CreateUserAsync(Ct);
        await factory.InsertItemAsync(viewer.Id, "ust-giyim/tisort", ProductColor.Red, cancellationToken: Ct);
        var forViewer = await viewer.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Equal("similar", forViewer.GetProperty("tags")[0].GetProperty("inWardrobe").GetString());
        Assert.Equal(1, forViewer.GetProperty("summary").GetProperty("ownedCount").GetInt32());
        var forAuthor = await author.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Equal("exact", forAuthor.GetProperty("tags")[0].GetProperty("inWardrobe").GetString());
    }

    [Fact]
    public async Task PublishRejectsSomeoneElsesWardrobeItemAndProcessingPosts()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var foreignItem = await factory.InsertItemAsync(other.Id, "ust-giyim/tisort", ProductColor.Black, cancellationToken: Ct);

        var postId = await factory.CreateProcessedPostAsync(author.Client, cancellationToken: Ct);
        using var foreign = await author.Client.PublishRawAsync(postId, "Public", [SocialClientExtensions.WardrobeTag(foreignItem)], cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("Social.InvalidTagTarget", (await foreign.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var invalid = await author.Client.PublishRawAsync(postId, "Everyone", [new { kind = "Product", x = 0.1, y = 0.1, w = 0.2, h = 0.2 }], cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.ReadProblemAsync(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("visibility", out _));
        Assert.True(errors.TryGetProperty("tags[0].productId", out _));

        await author.Client.AcceptConsentAsync(Ct);
        using var created = await author.Client.CreatePostUploadUrlRawAsync(cancellationToken: Ct);
        var processing = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("postId").GetGuid();
        using var early = await author.Client.PublishRawAsync(processing, "Public", [], cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("Social.PostProcessing", (await early.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AnUnsafePhotoIsBlockedWithAReasonOnlyTheAuthorSees()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var viewer = await factory.CreateUserAsync(Ct);

        var postId = await factory.CreateProcessedPostAsync(author.Client, "nsfw", Ct);

        var row = await factory.PostRowAsync(postId, Ct);
        Assert.Equal((PostStatus.Blocked, ModerationVerdict.Block), (row.Status, row.ModerationVerdict));
        Assert.Null(row.ImageFeedKey);
        Assert.Empty(row.Tags);

        var mine = await author.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Equal("Blocked", mine.GetProperty("status").GetString());
        Assert.Equal("Fotoğraf topluluk kurallarına uymuyor.", mine.GetProperty("statusMessage").GetString());
        Assert.Equal(HttpStatusCode.NotFound, await viewer.Client.StatusOfAsync(postId, Ct));

        using var publish = await author.Client.PublishRawAsync(postId, "Public", [], cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
    }

    [Fact]
    public async Task ABorderlinePhotoWaitsForAReviewerEvenWhenConfirmed()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();

        var postId = await factory.CreateProcessedPostAsync(author.Client, "borderline", Ct);
        var confirmed = await author.Client.PublishAsync(postId, "Public", [], cancellationToken: Ct);
        Assert.Equal("NeedsReview", confirmed.GetProperty("status").GetString());
        Assert.Equal(SocialCopy.InReview, confirmed.GetProperty("statusMessage").GetString());
        Assert.Equal(HttpStatusCode.NotFound, await anonymous.StatusOfAsync(postId, Ct));

        var queue = await (await anonymous.SendInternalAsync(HttpMethod.Get, "/api/v1/internal/social/review-queue", cancellationToken: Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains(queue.EnumerateArray(), item => item.GetProperty("postId").GetGuid() == postId);

        using var approve = await anonymous.SendInternalAsync(HttpMethod.Post, $"/api/v1/internal/social/posts/{postId}/review", new { decision = "Approve" }, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await anonymous.StatusOfAsync(postId, Ct));

        using var withoutKey = await anonymous.SendInternalAsync(HttpMethod.Post, $"/api/v1/internal/social/posts/{postId}/review", new { decision = "Remove" }, internalKey: null, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
    }

    [Fact]
    public async Task AnUnreachableVisionServiceIsRetriedAndNeverPublishesUnmoderated()
    {
        using var author = await factory.CreateUserAsync(Ct);

        var postId = await factory.CreateProcessedPostAsync(author.Client, "unreachable", Ct);
        Assert.Equal(PostStatus.Processing, (await factory.PostRowAsync(postId, Ct)).Status);

        for (var attempt = 2; attempt < PostProcessJob.MaxAttempts; attempt++)
        {
            Assert.Equal(PostProcessOutcome.Retry, await factory.ProcessPostAsync(postId, Ct));
        }

        Assert.Equal(PostProcessOutcome.Failed, await factory.ProcessPostAsync(postId, Ct));
        var row = await factory.PostRowAsync(postId, Ct);
        Assert.Equal(PostStatus.Failed, row.Status);
        Assert.Null(row.ImageFeedKey);
        Assert.Contains(factory.Jobs.JobsFor(postId), job => job.Type == typeof(PostProcessJob));
    }

    [Fact]
    public async Task AFailedDetectionLeavesAPublishableDraftWithoutProposals()
    {
        using var author = await factory.CreateUserAsync(Ct);

        var postId = await factory.CreateProcessedPostAsync(author.Client, "no-detect", Ct);

        var draft = await author.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.False(draft.TryGetProperty("proposals", out _));
        Assert.Equal("Live", (await author.Client.PublishAsync(postId, "Followers", [SocialClientExtensions.UnknownTag("Vintage kemer")], cancellationToken: Ct)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task VisibilityFollowsTheChoiceTheFollowGraphAndBlocks()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var follower = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        using var blocked = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();

        await follower.Client.FollowAsync(author.Id, Ct);
        await blocked.Client.FollowAsync(author.Id, Ct);
        await author.Client.BlockAsync(blocked.Id, Ct);

        var priv = await factory.CreateLivePostAsync(author.Client, "Private", cancellationToken: Ct);
        var followers = await factory.CreateLivePostAsync(author.Client, "Followers", cancellationToken: Ct);
        var pub = await factory.CreateLivePostAsync(author.Client, "Public", cancellationToken: Ct);

        var expected = new (HttpClient Client, string Who, HttpStatusCode Private, HttpStatusCode Followers, HttpStatusCode Public)[]
        {
            (author.Client, "author", HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK),
            (anonymous, "anonymous", HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.OK),
            (follower.Client, "follower", HttpStatusCode.NotFound, HttpStatusCode.OK, HttpStatusCode.OK),
            (stranger.Client, "stranger", HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.OK),
            (blocked.Client, "blocked", HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound),
        };

        foreach (var (client, who, privateStatus, followersStatus, publicStatus) in expected)
        {
            Assert.True(privateStatus == await client.StatusOfAsync(priv, Ct), $"{who} / private");
            Assert.True(followersStatus == await client.StatusOfAsync(followers, Ct), $"{who} / followers");
            Assert.True(publicStatus == await client.StatusOfAsync(pub, Ct), $"{who} / public");
        }

        // The block removed the follow and goes both ways: the blocker no longer sees the blocked user either.
        Assert.False(await factory.WithDbAsync(db => db.Follows.AnyAsync(follow => follow.FollowerId == blocked.Id && follow.FolloweeId == author.Id, Ct)));
        using var blockedProfile = await author.Client.GetAsync($"/api/v1/users/{blocked.Id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, blockedProfile.StatusCode);
        using var refollow = await blocked.Client.PostAsync($"/api/v1/users/{author.Id}/follow", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, refollow.StatusCode);

        // Lists apply the same rules: the follower sees two posts on the profile, a stranger one, the author all three.
        Assert.Equal(2, (await follower.Client.GetJsonAsync($"/api/v1/users/{author.Id}/posts", Ct)).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await anonymous.GetJsonAsync($"/api/v1/users/{author.Id}/posts", Ct)).GetProperty("totalCount").GetInt32());
        Assert.Equal(3, (await author.Client.GetJsonAsync($"/api/v1/users/{author.Id}/posts", Ct)).GetProperty("totalCount").GetInt32());
        var profile = await follower.Client.GetJsonAsync($"/api/v1/users/{author.Id}", Ct);
        Assert.Equal((1, 2, true), (profile.GetProperty("followerCount").GetInt32(), profile.GetProperty("postCount").GetInt32(), profile.GetProperty("isFollowing").GetBoolean()));
        Assert.Contains((await author.Client.GetJsonAsync("/api/v1/me/blocks", Ct)).EnumerateArray(), entry => entry.GetProperty("id").GetGuid() == blocked.Id);
    }

    [Fact]
    public async Task TheFeedPutsFollowedPostsFirstKeepsAuthorsApartAndHidesBlockedUsers()
    {
        using var viewer = await factory.CreateUserAsync(Ct);
        using var followed = await factory.CreateUserAsync(Ct);
        using var prolific = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        using var blocker = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();

        await viewer.Client.FollowAsync(followed.Id, Ct);
        var followersOnly = await factory.CreateLivePostAsync(followed.Client, "Followers", cancellationToken: Ct);
        var first = await factory.CreateLivePostAsync(prolific.Client, cancellationToken: Ct);
        var second = await factory.CreateLivePostAsync(prolific.Client, cancellationToken: Ct);
        var between = await factory.CreateLivePostAsync(other.Client, cancellationToken: Ct);
        var hidden = await factory.CreateLivePostAsync(blocker.Client, cancellationToken: Ct);
        await blocker.Client.BlockAsync(viewer.Id, Ct);
        var privatePost = await factory.CreateLivePostAsync(other.Client, "Private", cancellationToken: Ct);

        var feed = await viewer.Client.FeedIdsAsync(cancellationToken: Ct);
        Assert.Equal(followersOnly, feed[0]);
        Assert.Contains(first, feed);
        Assert.Contains(second, feed);
        Assert.Contains(between, feed);
        Assert.DoesNotContain(hidden, feed);
        Assert.DoesNotContain(privatePost, feed);
        Assert.NotEqual(1, Math.Abs(feed.IndexOf(first) - feed.IndexOf(second)));

        // No two neighbours share an author, whoever else posted in the shared database.
        var page = await viewer.Client.GetJsonAsync("/api/v1/feed", Ct);
        var authors = page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("author").GetProperty("id").GetGuid()).ToList();
        Assert.All(authors.Zip(authors.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        Assert.Equal("Following", page.GetProperty("items")[0].GetProperty("reason").GetString());

        var following = await viewer.Client.FeedIdsAsync("&mode=following", Ct);
        Assert.Equal([followersOnly], following);

        var anonymousFeed = await anonymous.FeedIdsAsync(cancellationToken: Ct);
        Assert.DoesNotContain(followersOnly, anonymousFeed);
        Assert.Contains(hidden, anonymousFeed);
        using var anonymousFollowing = await anonymous.GetAsync("/api/v1/feed?mode=following", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousFollowing.StatusCode);
        using var badMode = await anonymous.GetAsync("/api/v1/feed?mode=trending", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
    }

    [Fact]
    public async Task ThreeReportsHideAPostUntilAReviewerDecides()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var postId = await factory.CreateLivePostAsync(author.Client, cancellationToken: Ct);

        using var own = await author.Client.ReportAsync(postId, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);

        var reporters = new List<TestUser>();
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var reporter = await factory.CreateUserAsync(Ct);
                reporters.Add(reporter);
                using var report = await reporter.Client.ReportAsync(postId, index == 0 ? "Nudity" : "Spam", Ct);
                Assert.Equal(HttpStatusCode.Accepted, report.StatusCode);

                if (index == 0)
                {
                    using var again = await reporter.Client.ReportAsync(postId, cancellationToken: Ct);
                    Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
                }

                Assert.Equal(index < 2 ? HttpStatusCode.OK : HttpStatusCode.NotFound, await anonymous.StatusOfAsync(postId, Ct));
            }
        }
        finally
        {
            reporters.ForEach(reporter => reporter.Dispose());
        }

        var mine = await author.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Equal("Hidden", mine.GetProperty("status").GetString());
        Assert.Equal(SocialCopy.HiddenAfterReports, mine.GetProperty("statusMessage").GetString());

        using var remove = await anonymous.SendInternalAsync(HttpMethod.Post, $"/api/v1/internal/social/posts/{postId}/review", new { decision = "Remove" }, cancellationToken: Ct);
        Assert.Equal("Blocked", (await remove.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        Assert.Equal(SocialCopy.RemovedAfterReview, (await author.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct)).GetProperty("statusMessage").GetString());
    }

    [Fact]
    public async Task DeletingAPostHidesItAtOnceAndThePurgeRemovesEveryStoredObject()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var fan = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateLivePostAsync(author.Client, cancellationToken: Ct);
        using (var like = await fan.Client.PostAsync($"/api/v1/posts/{postId}/like", null, Ct))
        {
            Assert.Equal(1, (await like.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("likeCount").GetInt32());
        }

        var row = await factory.PostRowAsync(postId, Ct);
        string[] keys = [row.ImageOriginalKey!, row.ImageFeedKey!, row.ImagePreviewKey!];
        Assert.All(keys, key => Assert.NotNull(factory.Storage.Find(key)));

        using var delete = await author.Client.DeleteAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await author.Client.StatusOfAsync(postId, Ct));
        Assert.Equal(HttpStatusCode.NotFound, await fan.Client.StatusOfAsync(postId, Ct));

        var summary = await factory.PurgePostsAsync(author.Id, Ct);
        Assert.Equal((1, 3), (summary.PostsPurged, summary.ObjectsDeleted));
        Assert.All(keys, key => Assert.Null(factory.Storage.Find(key)));
        Assert.False(await factory.WithDbAsync(db => db.Posts.AnyAsync(post => post.Id == postId, Ct)));
        Assert.False(await factory.WithDbAsync(db => db.PostLikes.AnyAsync(like => like.PostId == postId, Ct)));
        Assert.Equal(new PurgeSummary(0, 0), await factory.PurgePostsAsync(author.Id, Ct));
    }

    [Fact]
    public async Task ACreatorsTagEarnsTheClickAndTheReferralButNeverTheirOwnClick()
    {
        using var creator = await factory.CreateUserAsync(Ct);
        using var regular = await factory.CreateUserAsync(Ct);
        using var viewer = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var productId = await anonymous.SeedProductAsync("ust-giyim/gomlek", "White", 3, price: 650m, cancellationToken: Ct);
        var otherProduct = await anonymous.SeedProductAsync("alt-giyim/pantolon", "Black", 3, cancellationToken: Ct);
        var handle = $"kreator_{Guid.NewGuid():N}"[..20];
        await anonymous.MakeCreatorAsync(creator.Id, handle, Ct);

        var postId = await factory.CreateLivePostAsync(creator.Client, tags: [SocialClientExtensions.ProductTag(productId)], cancellationToken: Ct);
        var post = await viewer.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.True(post.GetProperty("author").GetProperty("isCreator").GetBoolean());
        Assert.Equal(handle, post.GetProperty("author").GetProperty("handle").GetString());
        Assert.Equal("Bu içerikten komisyon kazanabilir", post.GetProperty("disclosure").GetString());
        var goPath = post.GetProperty("tags")[0].GetProperty("offer").GetProperty("goPath").GetString()!;

        var click = await ClickAsync(viewer.Client, goPath);
        Assert.Equal((postId, creator.Id, "feed"), (click.PostId, click.CreatorId, click.Source));
        var referral = await factory.WithDbAsync(db => db.ReferralEvents.AsNoTracking().SingleAsync(row => row.ClickId == click.Id, Ct));
        Assert.Equal((ReferralKind.Click, creator.Id, postId, 40m, viewer.Id), (referral.Kind, referral.CreatorId, referral.PostId, referral.SharePercent, referral.ViewerUserId));

        // Anonymous clicks count too; the creator's own click is recorded for the post but earns nothing.
        Assert.Equal(creator.Id, (await ClickAsync(anonymous, goPath)).CreatorId);
        var own = await ClickAsync(creator.Client, goPath);
        Assert.Equal((postId, (Guid?)null), (own.PostId, own.CreatorId));

        // A forged post parameter on another product's offer attributes nothing.
        var otherOffer = await factory.WithDbAsync(db => db.Offers.Where(offer => offer.ProductId == otherProduct).Select(offer => offer.Id).SingleAsync(Ct));
        var forged = await ClickAsync(viewer.Client, $"/api/v1/go/{otherOffer}?src=feed&post={postId}");
        Assert.Equal(((Guid?)null, (Guid?)null), (forged.PostId, forged.CreatorId));

        // A regular user's post records the post, no creator.
        var regularPost = await factory.CreateLivePostAsync(regular.Client, tags: [SocialClientExtensions.ProductTag(productId)], cancellationToken: Ct);
        var regularGo = (await viewer.Client.GetJsonAsync($"/api/v1/posts/{regularPost}", Ct)).GetProperty("tags")[0].GetProperty("offer").GetProperty("goPath").GetString()!;
        var regularClick = await ClickAsync(viewer.Client, regularGo);
        Assert.Equal((regularPost, (Guid?)null), (regularClick.PostId, regularClick.CreatorId));

        // The creator's stats: two attributed clicks and an estimate, labelled as one.
        var stats = await creator.Client.GetJsonAsync("/api/v1/me/creator/stats", Ct);
        Assert.Equal(2, stats.GetProperty("clicks").GetInt32());
        var estimate = stats.GetProperty("estimatedEarnings");
        Assert.True(estimate.GetProperty("isEstimate").GetBoolean());
        Assert.Equal(CreatorEarnings.Estimate(2, new CreatorOptions()).Amount, estimate.GetProperty("amount").GetDecimal());
        Assert.Equal(postId, stats.GetProperty("topPosts")[0].GetProperty("postId").GetGuid());
        using var notCreator = await regular.Client.GetAsync("/api/v1/me/creator/stats", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, notCreator.StatusCode);

        var page = await anonymous.GetJsonAsync($"/api/v1/creators/{handle}", Ct);
        Assert.Equal(creator.Id, page.GetProperty("profile").GetProperty("id").GetGuid());
        Assert.Equal(1, page.GetProperty("posts").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task TryingAPostWithMyWardrobeAnchorsItsTaggedProductsAndKeepsTheAttribution()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var viewer = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var shirt = await anonymous.SeedProductAsync("ust-giyim/gomlek", "White", 3, price: 700m, cancellationToken: Ct);
        await factory.InsertWardrobeAsync(viewer.Id, RecommendationClientExtensions.FortyPieces, Ct);

        var postId = await factory.CreateLivePostAsync(author.Client, tags: [SocialClientExtensions.ProductTag(shirt), SocialClientExtensions.UnknownTag("Şapka")], cancellationToken: Ct);

        using var response = await viewer.Client.PostAsJsonAsync($"/api/v1/posts/{postId}/try", new { seed = 0 }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var group = Assert.Single(body.GetProperty("groups").EnumerateArray());
        Assert.Equal(shirt, group.GetProperty("productId").GetGuid());
        var outfits = group.GetProperty("outfits").EnumerateArray().ToList();
        Assert.NotEmpty(outfits);
        var anchor = outfits[0].Pieces().Single(piece => piece.GetProperty("role").GetString() == "Anchor");
        Assert.Equal(shirt, anchor.GetProperty("productId").GetGuid());
        Assert.EndsWith($"?src=feed&post={postId}", anchor.GetProperty("offer").GetProperty("goPath").GetString(), StringComparison.Ordinal);

        var untaggable = await factory.CreateLivePostAsync(author.Client, tags: [SocialClientExtensions.UnknownTag("Kolye")], cancellationToken: Ct);
        using var nothing = await viewer.Client.PostAsJsonAsync($"/api/v1/posts/{untaggable}/try", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);
    }

    [Fact]
    public async Task ASavedOutfitBecomesACollagePostWhosePiecesAreModeratedAndConfirmed()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.AcceptConsentAsync(Ct);
        var seeds = await factory.InsertWardrobeAsync(user.Id, RecommendationClientExtensions.FortyPieces, Ct);
        var daily = await user.Client.DailyAsync(cancellationToken: Ct);
        var outfitId = daily.GetProperty("outfit").GetProperty("id").GetGuid();
        using (var save = await user.Client.PostOutfitAsync(outfitId, "save", cancellationToken: Ct))
        {
            Assert.True(save.IsSuccessStatusCode);
        }

        using var created = await user.Client.PostAsJsonAsync("/api/v1/posts/from-outfit", new { outfitId }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var post = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var postId = post.GetProperty("id").GetGuid();
        Assert.Equal(("Outfit", "Processing"), (post.GetProperty("kind").GetString(), post.GetProperty("status").GetString()));
        var proposals = post.GetProperty("proposals").EnumerateArray().ToList();
        Assert.True(proposals.Count >= 3);
        Assert.All(proposals, proposal => Assert.Equal("Outfit", proposal.GetProperty("source").GetString()));

        Assert.Equal(PostProcessOutcome.Draft, await factory.ProcessPostAsync(postId, Ct));

        object[] tags = [.. proposals.Select(proposal => new
        {
            kind = "Wardrobe",
            x = proposal.GetProperty("x").GetDouble(),
            y = proposal.GetProperty("y").GetDouble(),
            w = proposal.GetProperty("w").GetDouble(),
            h = proposal.GetProperty("h").GetDouble(),
            wardrobeItemId = proposal.GetProperty("wardrobeItemId").GetGuid(),
        })];
        var live = await user.Client.PublishAsync(postId, "Public", tags, cancellationToken: Ct);
        Assert.Equal("Live", live.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, live.GetProperty("image").ValueKind);
        Assert.Equal(proposals.Count, live.GetProperty("tags").GetArrayLength());
        Assert.All(live.GetProperty("tags").EnumerateArray(), tag => Assert.Equal("exact", tag.GetProperty("inWardrobe").GetString()));
        Assert.NotEmpty(seeds);

        using var unsaved = await user.Client.PostAsJsonAsync("/api/v1/posts/from-outfit", new { outfitId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unsaved.StatusCode);
    }

    [Fact]
    public async Task LikesAreIdempotentAndListedForTheViewer()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var fan = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateLivePostAsync(author.Client, cancellationToken: Ct);

        for (var index = 0; index < 2; index++)
        {
            using var like = await fan.Client.PostAsync($"/api/v1/posts/{postId}/like", null, Ct);
            Assert.Equal(1, (await like.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("likeCount").GetInt32());
        }

        Assert.True((await fan.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct)).GetProperty("likedByMe").GetBoolean());
        using var unlike = await fan.Client.DeleteAsync($"/api/v1/posts/{postId}/like", Ct);
        Assert.Equal(0, (await unlike.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("likeCount").GetInt32());

        var mine = await author.Client.GetJsonAsync("/api/v1/me/posts", Ct);
        Assert.Equal(postId, mine.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task WardrobeStatsCountThePiecesThatPairWithNothing()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.White, cancellationToken: Ct);
        await factory.InsertItemAsync(user.Id, "alt-giyim/jean", ProductColor.Blue, cancellationToken: Ct);
        await factory.InsertItemAsync(user.Id, "ust-giyim/gomlek", ProductColor.Black, cancellationToken: Ct);

        var stats = await user.Client.GetJsonAsync("/api/v1/me/wardrobe/stats", Ct);
        var grid = await user.Client.GetJsonAsync("/api/v1/me/wardrobe/items", Ct);
        var expected = grid.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("companionCount").GetInt32() == 0);

        Assert.Equal(expected, stats.GetProperty("unmatchedCount").GetInt32());

        using var empty = await factory.CreateUserAsync(Ct);
        Assert.Equal(0, (await empty.Client.GetJsonAsync("/api/v1/me/wardrobe/stats", Ct)).GetProperty("unmatchedCount").GetInt32());
    }

    private async Task<Persistence.Entities.Engagement.Click> ClickAsync(HttpClient client, string goPath)
    {
        using var response = await client.GetAsync(goPath, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = response.Headers.Location!.Query;
        var clickId = long.Parse(query.Split('&').Single(pair => pair.Contains("kombin_click=", StringComparison.Ordinal)).Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);

        return await factory.WithDbAsync(db => db.Clicks.AsNoTracking().SingleAsync(click => click.Id == clickId, Ct));
    }

    /// <summary>The orientation of the minimal EXIF the stripper writes back (0 when there is none).</summary>
    private static int ImageMetadataOrientation(byte[] jpeg)
    {
        var marker = Encoding.Latin1.GetString(jpeg).IndexOf("Exif\0\0MM", StringComparison.Ordinal);

        return marker < 0 ? 0 : jpeg[marker + 6 + 8 + 2 + 9];
    }
}
