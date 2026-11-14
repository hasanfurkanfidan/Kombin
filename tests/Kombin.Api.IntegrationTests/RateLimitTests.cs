using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Common.RateLimiting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The rate limiter, on hosts of their own (the shared host runs with <c>RateLimits:Enabled=false</c>) with tiny limits, so a test spends a
/// handful of requests. Each test uses its own client address (<c>X-Test-Remote-Ip</c>) and its own users, so hosts and tests never share a bucket.
/// </summary>
public sealed class RateLimitTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int NextAddress;

    private static string NewAddress()
    {
        var number = Interlocked.Increment(ref NextAddress);

        return $"10.77.{(number >> 8) & 255}.{number & 255}";
    }

    private WebApplicationFactory<Program> Limited(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new[] { ("RateLimits:Enabled", "true") }.Concat(settings).Select(setting => new KeyValuePair<string, string?>(setting.Item1, setting.Item2)))));

    private static HttpClient ClientAt(WebApplicationFactory<Program> host, string? address = null)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.Header, address ?? NewAddress());

        return client;
    }

    /// <summary>A multipart body with an <c>image</c> field: it gets past the content-type matching to the endpoint (whose own answer does not matter here).</summary>
    private static MultipartFormDataContent Photo()
    {
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

        return new MultipartFormDataContent { { file, "image", "photo.jpg" } };
    }

    private static Task<HttpResponseMessage> WrongLoginAsync(HttpClient client) =>
        client.LoginAsync(ApiClientExtensions.NewEmail(), "Wrong-password-1", cancellationToken: Ct);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? address = null, string? forwardedFor = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        if (address is not null)
        {
            request.Headers.Add(TestRemoteIpStartupFilter.Header, address);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        using var _ = response;
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // A Retry-After in whole seconds, and the usual problem document with the stable code.
        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter, System.Globalization.CultureInfo.InvariantCulture), 1, 3600);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("RateLimited", problem.GetProperty("errorCode").GetString());
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal(int.Parse(retryAfter, System.Globalization.CultureInfo.InvariantCulture), problem.GetProperty("retryAfterSeconds").GetInt32());

        // Like every other response, it carries the correlation ID, in the header and in the body.
        var correlation = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
        Assert.Equal(correlation, problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task TheDefaultsAreTheLaunchLimits()
    {
        var limits = factory.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value;

        Assert.Equal((10, 300), (limits.Auth.PermitLimit, limits.Auth.WindowSeconds));
        Assert.Equal((20, 3600), (limits.Lens.Anonymous.PermitLimit, limits.Lens.Anonymous.WindowSeconds));
        Assert.Equal((60, 3600), (limits.Lens.Authenticated.PermitLimit, limits.Lens.Authenticated.WindowSeconds));
        Assert.Equal((60, 3600), (limits.Uploads.PermitLimit, limits.Uploads.WindowSeconds));
        Assert.Equal((600, 300), (limits.Events.PermitLimit, limits.Events.WindowSeconds));
        Assert.Equal((120, 60), (limits.Go.PermitLimit, limits.Go.WindowSeconds));
        Assert.Equal((600, 60), (limits.Global.PermitLimit, limits.Global.WindowSeconds));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheSharedHostHasTheLimitsOffSoManyRequestsAreNeverRefused()
    {
        using var client = ClientAt(factory);

        for (var attempt = 0; attempt < 15; attempt++)
        {
            using var response = await WrongLoginAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task EveryAuthEndpointSharesOneBucketPerAddressAndTheRefusalIsAProblemDocument()
    {
        using var host = Limited(("RateLimits:Auth:PermitLimit", "3"));
        using var client = ClientAt(host);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await WrongLoginAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await AssertRateLimitedAsync(await WrongLoginAsync(client));

        // Register, external sign-in, refresh, cancel-deletion and DELETE /me draw from the same bucket.
        await AssertRateLimitedAsync(await client.RegisterAsync(ApiClientExtensions.NewEmail(), cancellationToken: Ct));
        await AssertRateLimitedAsync(await client.ExternalSignInAsync("google", "invalid", cancellationToken: Ct));
        await AssertRateLimitedAsync(await client.RefreshAsync("nope", Ct));
        await AssertRateLimitedAsync(await client.PostAsJsonAsync("/api/v1/auth/cancel-deletion", new { email = ApiClientExtensions.NewEmail(), password = "x" }, Ct));
        await AssertRateLimitedAsync(await SendAsync(client, HttpMethod.Delete, "/api/v1/me", content: JsonContent.Create(new { confirm = "SİL" })));

        // Another address has its own bucket, and so do endpoints outside the group.
        using var other = ClientAt(host);
        using var fresh = await WrongLoginAsync(other);
        Assert.Equal(HttpStatusCode.Unauthorized, fresh.StatusCode);
        using var catalog = await client.GetAsync("/api/v1/catalog/brands", Ct);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, catalog.StatusCode);
    }

    [Fact]
    public async Task LensAllowsLessToAnAnonymousAddressThanToASignedInUser()
    {
        using var host = Limited(("RateLimits:Lens:Anonymous:PermitLimit", "2"), ("RateLimits:Lens:Authenticated:PermitLimit", "3"));
        using var anonymous = ClientAt(host);
        var address = NewAddress();

        // The body is not a photo, so the answer is a refusal of its own; the limiter counts the request all the same.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await SendAsync(anonymous, HttpMethod.Post, "/api/v1/lens/search", address, content: Photo());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        await AssertRateLimitedAsync(await SendAsync(anonymous, HttpMethod.Post, "/api/v1/lens/search", address, content: Photo()));

        // A signed-in user on the very same address is counted as the user, with the larger limit.
        using var userClient = ClientAt(host, address);
        var tokens = await userClient.RegisterAndReadAsync(cancellationToken: Ct);
        userClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await SendAsync(userClient, HttpMethod.Post, "/api/v1/lens/search", content: Photo());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        await AssertRateLimitedAsync(await SendAsync(userClient, HttpMethod.Post, "/api/v1/lens/search", content: Photo()));

        // Another user has a bucket of their own.
        using var secondClient = ClientAt(host, address);
        var second = await secondClient.RegisterAndReadAsync(cancellationToken: Ct);
        secondClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", second.AccessToken);
        using var secondResponse = await SendAsync(secondClient, HttpMethod.Post, "/api/v1/lens/search", content: Photo());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
    }

    [Fact]
    public async Task UploadUrlsAreLimitedPerUserAcrossTheWardrobeAndPosts()
    {
        using var host = Limited(("RateLimits:Uploads:PermitLimit", "2"));
        using var client = ClientAt(host);
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.CreateUploadUrlRawAsync(cancellationToken: Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await AssertRateLimitedAsync(await client.CreateUploadUrlRawAsync(cancellationToken: Ct));

        // The post upload URL (refused for lack of consent) draws from the same bucket.
        await AssertRateLimitedAsync(await client.CreatePostUploadUrlRawAsync(cancellationToken: Ct));

        using var other = ClientAt(host);
        var otherTokens = await other.RegisterAndReadAsync(cancellationToken: Ct);
        other.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", otherTokens.AccessToken);
        using var otherResponse = await other.CreateUploadUrlRawAsync(cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Created, otherResponse.StatusCode);
    }

    [Fact]
    public async Task EventsAndTheGoRedirectHaveTheirOwnLimits()
    {
        using var host = Limited(("RateLimits:Events:PermitLimit", "2"), ("RateLimits:Go:PermitLimit", "2"));
        using var client = ClientAt(host);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var events = await client.PostEventsAsync([], Ct);
            Assert.Equal(HttpStatusCode.BadRequest, events.StatusCode);
            using var go = await client.GetAsync($"/api/v1/go/{Guid.NewGuid()}", Ct);
            Assert.Equal(HttpStatusCode.NotFound, go.StatusCode);
        }

        await AssertRateLimitedAsync(await client.PostEventsAsync([], Ct));
        await AssertRateLimitedAsync(await client.GetAsync($"/api/v1/go/{Guid.NewGuid()}", Ct));
    }

    [Fact]
    public async Task TheGlobalFallbackCoversEverythingElseExceptTheHealthProbes()
    {
        using var host = Limited(("RateLimits:Global:PermitLimit", "3"));
        using var client = ClientAt(host);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await client.GetAsync("/api/v1/catalog/brands", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await AssertRateLimitedAsync(await client.GetAsync("/api/v1/catalog/brands", Ct));

        // Probes are never refused, however many there are.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var live = await client.GetAsync("/health/live", Ct);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using var ready = await client.GetAsync("/health", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
    }

    [Fact]
    public async Task BehindTheProxyTheLimiterCountsTheForwardedClientAndIgnoresAForgedHeader()
    {
        using var host = Limited(("RateLimits:Auth:PermitLimit", "2"));
        using var client = host.CreateClient();
        const string proxy = "172.18.0.5";

        // From the proxy (inside the known network) the X-Forwarded-For client is who counts: two clients, two buckets.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", proxy, "203.0.113.1", JsonContent.Create(new { email = ApiClientExtensions.NewEmail(), password = "Wrong-password-1" }));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await AssertRateLimitedAsync(await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", proxy, "203.0.113.1", JsonContent.Create(new { email = ApiClientExtensions.NewEmail(), password = "Wrong-password-1" })));
        using var otherClient = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", proxy, "203.0.113.2", JsonContent.Create(new { email = ApiClientExtensions.NewEmail(), password = "Wrong-password-1" }));
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);

        // A client that prepends its own entries cannot pick its bucket: the proxy's entry (the last one) is the one believed.
        await AssertRateLimitedAsync(await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", proxy, "198.51.100.77, 203.0.113.1", JsonContent.Create(new { email = ApiClientExtensions.NewEmail(), password = "Wrong-password-1" })));

        // From an address outside the known network the header is not believed at all, so changing it does not reset the count.
        const string stranger = "8.8.8.8";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", stranger, $"203.0.113.{100 + attempt}", JsonContent.Create(new { email = ApiClientExtensions.NewEmail(), password = "Wrong-password-1" }));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await AssertRateLimitedAsync(await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", stranger, "203.0.113.200", JsonContent.Create(new { email = ApiClientExtensions.NewEmail(), password = "Wrong-password-1" })));
    }
}
