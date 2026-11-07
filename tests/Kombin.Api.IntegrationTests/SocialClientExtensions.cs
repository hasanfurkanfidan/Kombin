using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Social;
using Kombin.Api.Features.Social.Jobs;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

internal static class SocialClientExtensions
{
    public static async Task AcceptConsentAsync(this HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/social/consent", new { version = PostingConsent.Current.Version }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static Task<HttpResponseMessage> CreatePostUploadUrlRawAsync(this HttpClient client, string contentType = "image/jpeg", long sizeBytes = 2048, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync("/api/v1/posts/upload-url", new { contentType, sizeBytes }, cancellationToken);

    /// <summary>Consent (once per user is enough, it is idempotent), upload URL, the photo "uploaded" to storage, <c>process</c>, then the job. Returns the post ID.</summary>
    public static async Task<Guid> CreateProcessedPostAsync(this ApiFactory factory, HttpClient client, string flavor = "ok", CancellationToken cancellationToken = default)
    {
        await client.AcceptConsentAsync(cancellationToken);

        using var created = await client.CreatePostUploadUrlRawAsync(cancellationToken: cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var postId = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("postId").GetGuid();

        var key = await factory.WithDbAsync(db => db.Posts.Where(post => post.Id == postId).Select(post => post.ImageOriginalKey!).SingleAsync(cancellationToken));
        factory.Storage.Upload(key, FakeVisionClient.PostPhoto(flavor), "image/jpeg");

        using var process = await client.PostAsync($"/api/v1/posts/{postId}/process", null, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);

        await factory.ProcessPostAsync(postId, cancellationToken);

        return postId;
    }

    public static Task<PostProcessOutcome> ProcessPostAsync(this ApiFactory factory, Guid postId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<PostProcessJob, PostProcessOutcome>(job => job.ProcessAsync(postId, cancellationToken));

    public static Task<PurgeSummary> PurgePostsAsync(this ApiFactory factory, Guid authorId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<SocialPurgeJob, PurgeSummary>(job => job.PurgeAsync(authorId, cancellationToken));

    public static Task<HttpResponseMessage> PublishRawAsync(this HttpClient client, Guid postId, string visibility, object[] tags, string? caption = null, CancellationToken cancellationToken = default) =>
        client.PutAsJsonAsync($"/api/v1/posts/{postId}/publish", new { caption, visibility, tags }, cancellationToken);

    public static async Task<JsonElement> PublishAsync(this HttpClient client, Guid postId, string visibility, object[] tags, string? caption = null, CancellationToken cancellationToken = default)
    {
        using var response = await client.PublishRawAsync(postId, visibility, tags, caption, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>A processed photo post published with the given tags (none by default).</summary>
    public static async Task<Guid> CreateLivePostAsync(this ApiFactory factory, HttpClient client, string visibility = "Public", object[]? tags = null, CancellationToken cancellationToken = default)
    {
        var postId = await factory.CreateProcessedPostAsync(client, cancellationToken: cancellationToken);
        await client.PublishAsync(postId, visibility, tags ?? [], cancellationToken: cancellationToken);

        return postId;
    }

    public static object ProductTag(Guid productId, double y = 0.5) => new { kind = "Product", x = 0.3, y, w = 0.3, h = 0.2, productId };

    public static object WardrobeTag(Guid wardrobeItemId, double y = 0.2) => new { kind = "Wardrobe", x = 0.3, y, w = 0.3, h = 0.2, wardrobeItemId };

    public static object UnknownTag(string label) => new { kind = "Unknown", x = 0.1, y = 0.8, w = 0.2, h = 0.15, label };

    public static Task<HttpResponseMessage> GetPostRawAsync(this HttpClient client, Guid postId, CancellationToken cancellationToken = default) =>
        client.GetAsync($"/api/v1/posts/{postId}", cancellationToken);

    public static async Task<HttpStatusCode> StatusOfAsync(this HttpClient client, Guid postId, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetPostRawAsync(postId, cancellationToken);
        return response.StatusCode;
    }

    public static async Task FollowAsync(this HttpClient client, Guid userId, CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsync($"/api/v1/users/{userId}/follow", null, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static async Task BlockAsync(this HttpClient client, Guid userId, CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsync($"/api/v1/users/{userId}/block", null, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public static Task<HttpResponseMessage> ReportAsync(this HttpClient client, Guid postId, string reason = "Spam", CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync($"/api/v1/posts/{postId}/report", new { reason }, cancellationToken);

    public static async Task<List<Guid>> FeedIdsAsync(this HttpClient client, string query = "", CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>();
        for (var page = 1; page <= 20; page++)
        {
            var body = await client.GetJsonAsync($"/api/v1/feed?page={page}{query}", cancellationToken);
            ids.AddRange(body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            if (!body.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
        }

        return ids;
    }

    public static async Task<JsonElement> MakeCreatorAsync(this HttpClient client, Guid userId, string handle, CancellationToken cancellationToken = default)
    {
        using var response = await client.SendInternalAsync(HttpMethod.Put, $"/api/v1/internal/social/creators/{userId}", new { handle, bio = "Sokak stili", link = "https://example.com/me" }, cancellationToken: cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static Task<Post> PostRowAsync(this ApiFactory factory, Guid postId, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.Posts.AsNoTracking().Include(post => post.Tags).SingleAsync(post => post.Id == postId, cancellationToken));
}
