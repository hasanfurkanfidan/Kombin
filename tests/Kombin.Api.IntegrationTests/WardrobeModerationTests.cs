using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Common.Vision;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Security review M3: wardrobe photos go through the safety filter, and only an allowed photo is ever shown to another user. A blocked or
/// borderline piece (or one not moderated yet) stays fully usable in its owner's wardrobe; viewers get its tag without the image.
/// </summary>
public sealed class WardrobeModerationTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ok", "Allow")]
    [InlineData("nsfw", "Block")]
    [InlineData("borderline", "Review")]
    public async Task TheAnalysisStoresTheVerdictAndTheOwnerKeepsThePiece(string flavor, string expected)
    {
        using var owner = await factory.CreateUserAsync(Ct);

        var itemId = await factory.AddPhotoItemAsync(owner.Client, flavor, Ct);

        var row = await factory.ItemRowAsync(itemId, Ct);
        Assert.Equal((WardrobeItemStatus.Ready, Enum.Parse<ModerationVerdict>(expected)), (row.Status, row.ModerationVerdict));
        Assert.NotNull(row.ModeratedAt);
        var item = await owner.Client.GetItemAsync(itemId, Ct);
        Assert.False(string.IsNullOrEmpty(item.GetProperty("thumbUrl").GetString()));
    }

    [Theory]
    [InlineData("nsfw")]
    [InlineData("borderline")]
    [InlineData("moderation-down")]
    public async Task AnUnallowedPieceIsTaggedWithoutItsImageForEveryoneButItsOwner(string flavor)
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var viewer = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var itemId = await factory.AddPhotoItemAsync(author.Client, flavor, Ct);
        var thumbKey = (await factory.ItemRowAsync(itemId, Ct)).ImageThumbKey!;
        var productId = await anonymous.SeedProductAsync("ust-giyim/gomlek", "White", 3, cancellationToken: Ct);
        var postId = await factory.CreateLivePostAsync(author.Client, tags: [SocialClientExtensions.WardrobeTag(itemId), SocialClientExtensions.ProductTag(productId)], cancellationToken: Ct);

        var asAuthor = await author.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
        Assert.Contains(thumbKey, WardrobeTagOf(asAuthor, itemId).GetProperty("imageUrl").GetString(), StringComparison.Ordinal);

        foreach (var client in new[] { viewer.Client, anonymous })
        {
            var post = await client.GetJsonAsync($"/api/v1/posts/{postId}", Ct);
            var tag = WardrobeTagOf(post, itemId);
            Assert.Equal(JsonValueKind.Null, tag.GetProperty("imageUrl").ValueKind);
            Assert.False(string.IsNullOrEmpty(tag.GetProperty("name").GetString()));

            // Nor through any other way a viewer reaches the post: the author's list, the feed, "Dolabımla dene".
            foreach (var path in new[] { $"/api/v1/users/{author.Id}/posts", "/api/v1/feed" })
            {
                using var response = await client.GetAsync(path, Ct);
                Assert.DoesNotContain(thumbKey, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }
        }

        using var tried = await viewer.Client.PostAsJsonAsync($"/api/v1/posts/{postId}/try", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, tried.StatusCode);
        Assert.DoesNotContain(thumbKey, await tried.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAllowedPieceShowsItsImageToViewers()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var viewer = await factory.CreateUserAsync(Ct);
        var itemId = await factory.AddPhotoItemAsync(author.Client, cancellationToken: Ct);
        var postId = await factory.CreateLivePostAsync(author.Client, tags: [SocialClientExtensions.WardrobeTag(itemId)], cancellationToken: Ct);

        var tag = WardrobeTagOf(await viewer.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct), itemId);

        Assert.Contains((await factory.ItemRowAsync(itemId, Ct)).ImageThumbKey!, tag.GetProperty("imageUrl").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingVerdictIsAskedForAgainByTheCleanupAndThenTheImageIsShared()
    {
        using var author = await factory.CreateUserAsync(Ct);
        using var viewer = await factory.CreateUserAsync(Ct);
        var itemId = await factory.AddPhotoItemAsync(author.Client, "moderation-down", Ct);
        var row = await factory.ItemRowAsync(itemId, Ct);
        Assert.Null(row.ModerationVerdict);
        var postId = await factory.CreateLivePostAsync(author.Client, tags: [SocialClientExtensions.WardrobeTag(itemId)], cancellationToken: Ct);

        // The filter answers again (the stored cutout is now one it allows); the daily cleanup asks it.
        factory.Storage.Upload(row.ImageCutoutKey!, FakeVisionClient.Cutout, "image/png");
        var summary = await factory.CleanupWardrobeAsync(author.Id, Ct);

        Assert.Equal(1, summary.VerdictsRetried);
        Assert.Equal(ModerationVerdict.Allow, (await factory.ItemRowAsync(itemId, Ct)).ModerationVerdict);
        var tag = WardrobeTagOf(await viewer.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct), itemId);
        Assert.Equal(JsonValueKind.String, tag.GetProperty("imageUrl").ValueKind);
        Assert.Equal(0, (await factory.CleanupWardrobeAsync(author.Id, Ct)).VerdictsRetried);
    }

    [Fact]
    public async Task ACatalogPieceIsShareableAtOnce()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var productId = await user.Client.SeedCatalogProductAsync("ayakkabi/sneaker", "White", 2, cancellationToken: Ct);

        using var created = await user.Client.FromProductAsync(productId, cancellationToken: Ct);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        Assert.Equal(ModerationVerdict.Allow, (await factory.ItemRowAsync(itemId, Ct)).ModerationVerdict);
    }

    private static JsonElement WardrobeTagOf(JsonElement post, Guid itemId) =>
        post.GetProperty("tags").EnumerateArray().Single(tag => tag.GetProperty("wardrobeItemId").ValueKind == JsonValueKind.String && tag.GetProperty("wardrobeItemId").GetGuid() == itemId);
}
