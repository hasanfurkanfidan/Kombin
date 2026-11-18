using System.Net;
using Amazon.Runtime;
using Kombin.Api.Common.Storage;

namespace Kombin.Api.Tests;

/// <summary>
/// Task B8 and B9: public images are stored with a one-day <c>Cache-Control</c> (never <c>immutable</c>, never longer: a deleted photo must leave every
/// cache within a day), and nothing private ever is public (a presigned link carries the header too, whatever the object was stored with).
/// </summary>
public class StorageCacheControlTests
{
    private const string OneDay = "public, max-age=86400";

    private const string Private = "private, no-store";

    private static readonly Guid User = Guid.Parse("3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11");

    private static readonly Guid Owner = Guid.Parse("7a1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    [Theory]
    [InlineData("wardrobe", "Cutout", "png", OneDay)]
    [InlineData("wardrobe", "Thumb", "webp", OneDay)]
    [InlineData("wardrobe", "Original", "jpg", Private)]
    [InlineData("posts", "Original", "jpg", Private)]
    [InlineData("posts", "Feed", "jpg", Private)]
    [InlineData("posts", "Preview", "webp", Private)]
    [InlineData("tryon", "Prepared", "png", Private)]
    [InlineData("tryon", "Render", "webp", Private)]
    [InlineData("tryon", "Render", "png", Private)]
    [InlineData("league", "Thumb", "webp", OneDay)]
    [InlineData("league", "Thumb", "jpg", OneDay)]
    public void EveryKeyIsCachedAsItsVisibilityAndItsAreaSay(string area, string variantName, string extension, string expected)
    {
        var key = StorageKeys.Build(area, User, Owner, Enum.Parse<ImageVariant>(variantName), extension);

        Assert.Equal(expected, StorageKeys.CacheControlFor(key));
    }

    [Fact]
    public void NoPublicImageIsEverCachedLongerThanADayOrAsImmutable()
    {
        foreach (var area in new[] { "wardrobe", "posts", "tryon", "league", "anything-new" })
        {
            foreach (var variant in new[] { ImageVariant.Cutout, ImageVariant.Thumb })
            {
                var header = StorageKeys.CacheControlFor(StorageKeys.Build(area, User, Owner, variant, "webp"));
                var maxAge = header.Split(',', StringSplitOptions.TrimEntries)
                    .Single(directive => directive.StartsWith("max-age=", StringComparison.Ordinal))["max-age=".Length..];

                Assert.True(int.Parse(maxAge, System.Globalization.CultureInfo.InvariantCulture) <= 86400, header);
                Assert.DoesNotContain("immutable", header, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void OnlyWhatTheBucketPolicyMakesPublicIsEverCachedPublicly()
    {
        foreach (var area in new[] { "wardrobe", "posts", "tryon", "league", "anything-new" })
        {
            foreach (var variant in Enum.GetValues<ImageVariant>())
            {
                var key = StorageKeys.Build(area, User, Owner, variant, "webp");
                var publicly = StorageKeys.CacheControlFor(key).StartsWith("public", StringComparison.Ordinal);

                Assert.Equal(StorageKeys.IsPublic(key), publicly);
            }
        }
    }

    [Fact]
    public async Task ThePutsHeaderAndThePrivateLinksOverrideAreWhatTheStoreSees()
    {
        var handler = new RecordingHandler();
        using var storage = new S3ObjectStorage(
            new StorageOptions
            {
                Endpoint = "http://minio.test:9000",
                Bucket = "kombin",
                AccessKey = "key",
                SecretKey = "secret",
                PublicBaseUrl = "http://minio.test:9000/kombin",
            },
            new RecordingFactory(handler));

        var thumb = StorageKeys.Build("wardrobe", User, Owner, ImageVariant.Thumb, "webp");
        var copy = StorageKeys.Build("league", Guid.NewGuid(), Guid.NewGuid(), ImageVariant.Thumb, "webp");
        var render = StorageKeys.Build("tryon", User, Owner, ImageVariant.Render, "webp");
        var original = StorageKeys.Build("wardrobe", User, Owner, ImageVariant.Original, "jpg");

        foreach (var key in new[] { thumb, copy, render, original })
        {
            await storage.PutAsync(key, [1, 2, 3], "image/webp", TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [(thumb, Directives(OneDay)), (copy, Directives(OneDay)), (render, Directives(Private)), (original, Directives(Private))],
            handler.Puts.Select(put => (put.Key, Directives(put.CacheControl))));

        // A presigned GET (only ever for non-public objects) asks the store to answer with a private Cache-Control, whatever was stored.
        var link = storage.GetPresignedDownloadUrl(render, TimeSpan.FromHours(1));
        Assert.Contains("response-cache-control=private%2C%20no-store", link.Replace("+", "%20", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The directives of a header value, sorted: the HTTP stack may reorder them.</summary>
    private static string Directives(string? header) =>
        string.Join(',', (header ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    private sealed record Put(string Key, string? CacheControl);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Put> Puts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                // The body is read so the SDK's stream is consumed like against a real store.
                if (request.Content is not null)
                {
                    await request.Content.ReadAsByteArrayAsync(cancellationToken);
                }

                var cacheControl = request.Headers.CacheControl?.ToString()
                    ?? (request.Content is not null && request.Content.Headers.TryGetValues("Cache-Control", out var values) ? string.Join(", ", values) : null)
                    ?? (request.Headers.TryGetValues("Cache-Control", out var raw) ? string.Join(", ", raw) : null);
                Puts.Add(new Put(Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'))["kombin/".Length..], cacheControl));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag\"");

            return response;
        }
    }

    private sealed class RecordingFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);
    }
}
