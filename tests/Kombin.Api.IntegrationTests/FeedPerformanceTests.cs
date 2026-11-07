using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Features.Social;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The feed's latency budget: <c>GET /feed</c> at most 250 ms p95 with 50,000 posts (2,000 authors over 60 days, two product tags each, a
/// viewer with a learned persona who follows 50 authors), so the SQL ranking, the persona match of 200 candidates and the page build are
/// all on the path. Synthetic, seeded data on its own PostgreSQL container, like <see cref="OutfitPerformanceTests"/>, and gated the same
/// way: it runs only with <c>KOMBIN_PERF=1</c>.
/// </summary>
public sealed class FeedPerformanceTests(FeedPerformanceTests.FeedPerformanceFactory factory) : IClassFixture<FeedPerformanceTests.FeedPerformanceFactory>
{
    public const int Posts = 50_000;

    public const int Authors = 2_000;

    public const int Products = 5_000;

    public const int Samples = 60;

    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static bool Enabled => OutfitPerformanceTests.Enabled;

    [Fact(Skip = OutfitPerformanceTests.SkipMessage, SkipUnless = nameof(Enabled))]
    public async Task TheFeedStaysWithinTwoHundredFiftyMillisecondsAtP95()
    {
        var (viewer, anonymous) = await factory.SeedAsync(Ct);

        for (var index = 0; index < 5; index++)
        {
            await MeasureAsync(viewer, "/api/v1/feed");
            await MeasureAsync(viewer, "/api/v1/feed?mode=following");
            await MeasureAsync(anonymous, "/api/v1/feed");
        }

        var forYou = new List<double>();
        var following = new List<double>();
        var signedOut = new List<double>();
        for (var index = 0; index < Samples; index++)
        {
            forYou.Add(await MeasureAsync(viewer, $"/api/v1/feed?page={(index % 3) + 1}"));
            following.Add(await MeasureAsync(viewer, $"/api/v1/feed?mode=following&page={(index % 2) + 1}"));
            signedOut.Add(await MeasureAsync(anonymous, "/api/v1/feed"));
        }

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"forYou    p50 {Percentile(forYou, 50):F1} ms  p95 {Percentile(forYou, 95):F1} ms  max {forYou.Max():F1} ms");
        output?.WriteLine($"following p50 {Percentile(following, 50):F1} ms  p95 {Percentile(following, 95):F1} ms  max {following.Max():F1} ms");
        output?.WriteLine($"anonymous p50 {Percentile(signedOut, 50):F1} ms  p95 {Percentile(signedOut, 95):F1} ms  max {signedOut.Max():F1} ms");

        Assert.True(Percentile(forYou, 95) <= Budget.TotalMilliseconds, $"forYou p95 {Percentile(forYou, 95):F1} ms");
        Assert.True(Percentile(following, 95) <= Budget.TotalMilliseconds, $"following p95 {Percentile(following, 95):F1} ms");
        Assert.True(Percentile(signedOut, 95) <= Budget.TotalMilliseconds, $"anonymous p95 {Percentile(signedOut, 95):F1} ms");
    }

    private static async Task<double> MeasureAsync(HttpClient client, string url)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        stopwatch.Stop();

        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("items").GetArrayLength() > 0, $"{url} returned no post");

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(List<double> samples, int percentile)
    {
        var sorted = samples.Order().ToList();
        return sorted[Math.Max(0, (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1)];
    }

    /// <summary>A third API host on its own database, configured like <see cref="ApiFactory"/>.</summary>
    public sealed class FeedPerformanceFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg18").Build();

        private readonly SemaphoreSlim _seedLock = new(1, 1);

        private (HttpClient Viewer, HttpClient Anonymous)? _seeded;

        public async ValueTask InitializeAsync()
        {
            if (!Enabled)
            {
                return;
            }

            await _postgres.StartAsync();
            _ = Services;
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _postgres.DisposeAsync();
            _seedLock.Dispose();
            GC.SuppressFinalize(this);
        }

        public async Task<(HttpClient Viewer, HttpClient Anonymous)> SeedAsync(CancellationToken cancellationToken)
        {
            await _seedLock.WaitAsync(cancellationToken);
            try
            {
                return _seeded ??= await SeedOnceAsync(cancellationToken);
            }
            finally
            {
                _seedLock.Release();
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage, FakeObjectStorage>();
                services.RemoveAll<IWeatherProvider>();
                services.AddSingleton<IWeatherProvider, FakeWeatherProvider>();
                services.RemoveAll<IBackgroundJobClient>();
                services.AddSingleton<IBackgroundJobClient, RecordingJobClient>();
            });

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                ["Database:MigrateOnStartup"] = "true",
                ["Database:SeedOnStartup"] = "false",
                ["Jwt:Issuer"] = "kombin-tests",
                ["Jwt:Audience"] = "kombin-tests",
                ["Jwt:SigningKey"] = "integration-test-signing-key-9f8e7d6c5b4a3",
                ["Internal:ApiKey"] = ApiFactory.InternalApiKey,
                ["Serilog:MinimumLevel:Default"] = "Warning",
                ["Hangfire:Enabled"] = "false",
            }));
        }

        private async Task<(HttpClient Viewer, HttpClient Anonymous)> SeedOnceAsync(CancellationToken cancellationToken)
        {
            var random = new Random(20261107);
            var now = DateTimeOffset.UtcNow;
            var colours = Enum.GetValues<ProductColor>();
            var fits = new[] { ProductFit.Slim, ProductFit.Regular, ProductFit.Relaxed, ProductFit.Oversized };

            var viewer = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var tokens = await viewer.RegisterAndReadAsync(cancellationToken: cancellationToken);
            viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            // A learned persona: the "match ≥ 60" filter and the scoring of 200 candidates are on the measured path.
            var personaId = await viewer.CreatePersonaAsync("Günlük", cancellationToken: cancellationToken);
            await viewer.SwipeAsync(personaId, ProfileClientExtensions.StyleCards.Take(12).Select(card => (card.Id, "Like")), cancellationToken);
            var viewerId = (await viewer.GetJsonAsync("/api/v1/me", cancellationToken)).GetProperty("id").GetGuid();

            Guid[] categoryIds;
            Guid brandId;
            await using (var scope = Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
                categoryIds = [.. await db.Categories.Where(category => category.ParentId != null && !category.Slug.StartsWith("aksesuar")).Select(category => category.Id).ToListAsync(cancellationToken)];
                var brand = new Brand { Name = "Feed Brand", Slug = "feed-brand" };
                db.Add(brand);
                await db.SaveChangesAsync(cancellationToken);
                brandId = brand.Id;
            }

            var productIds = new Guid[Products];
            var authorIds = new Guid[Authors];
            await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
            {
                await connection.OpenAsync(cancellationToken);

                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY catalog.products (id, brand_id, category_id, name, slug, audience, primary_color, pattern, fit, formality, seasons, is_active, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                    cancellationToken))
                {
                    for (var index = 0; index < Products; index++)
                    {
                        productIds[index] = Guid.CreateVersion7();
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(productIds[index], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(brandId, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(categoryIds[random.Next(categoryIds.Length)], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync($"Feed product {index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync($"feed-{index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync("Unisex", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(colours[random.Next(colours.Length)].ToString(), NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync("Solid", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(fits[random.Next(fits.Length)].ToString(), NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync((short)random.Next(1, 6), NpgsqlDbType.Smallint, cancellationToken);
                        await writer.WriteAsync((short)Season.AllYear, NpgsqlDbType.Smallint, cancellationToken);
                        await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY identity.users (id, email, password_hash, display_name, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    for (var index = 0; index < Authors; index++)
                    {
                        authorIds[index] = Guid.CreateVersion7();
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(authorIds[index], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync($"author-{index}@feed.example.com", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync("not-a-real-hash", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync($"Yazar {index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                // 50k posts over 60 days, mostly public; likes skewed (most posts few, some many).
                var postIds = new Guid[Posts];
                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY social.posts (id, author_id, kind, visibility, status, audience, consent_version, consent_at, image_feed_key, image_preview_key, image_aspect, like_count, report_count, processing_attempts, confirmed_at, published_at, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                    cancellationToken))
                {
                    for (var index = 0; index < Posts; index++)
                    {
                        postIds[index] = Guid.CreateVersion7();
                        var author = authorIds[random.Next(Authors)];
                        var published = now.AddMinutes(-random.Next(60 * 24 * 60));
                        var visibility = random.Next(10) switch { 0 => "Private", 1 or 2 => "Followers", _ => "Public" };

                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(postIds[index], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(author, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync("Photo", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(visibility, NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync("Live", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync("Unisex", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(PostingConsent.Current.Version, NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(published, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync($"posts/{author}/{postIds[index]}/feed.jpg", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync($"posts/{author}/{postIds[index]}/preview.webp", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(0.75, NpgsqlDbType.Double, cancellationToken);
                        await writer.WriteAsync((int)Math.Floor(Math.Pow(random.NextDouble(), 4) * 500), NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(published, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(published, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(published, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(published, NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY social.post_tags (id, post_id, position, x, y, w, h, kind, source, product_id, confirmed_at, created_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    for (var index = 0; index < Posts; index++)
                    {
                        for (var position = 0; position < 2; position++)
                        {
                            await writer.StartRowAsync(cancellationToken);
                            await writer.WriteAsync(Guid.CreateVersion7(), NpgsqlDbType.Uuid, cancellationToken);
                            await writer.WriteAsync(postIds[index], NpgsqlDbType.Uuid, cancellationToken);
                            await writer.WriteAsync(position, NpgsqlDbType.Integer, cancellationToken);
                            await writer.WriteAsync(0.3, NpgsqlDbType.Double, cancellationToken);
                            await writer.WriteAsync(0.1 + (0.4 * position), NpgsqlDbType.Double, cancellationToken);
                            await writer.WriteAsync(0.3, NpgsqlDbType.Double, cancellationToken);
                            await writer.WriteAsync(0.3, NpgsqlDbType.Double, cancellationToken);
                            await writer.WriteAsync("Product", NpgsqlDbType.Varchar, cancellationToken);
                            await writer.WriteAsync("User", NpgsqlDbType.Varchar, cancellationToken);
                            await writer.WriteAsync(productIds[random.Next(Products)], NpgsqlDbType.Uuid, cancellationToken);
                            await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                            await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        }
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY social.follows (follower_id, followee_id, created_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    foreach (var author in authorIds.Take(50))
                    {
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(viewerId, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(author, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY social.creator_profiles (user_id, handle, is_active, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    for (var index = 0; index < 50; index++)
                    {
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(authorIds[Authors - 1 - index], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync($"creator_{index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using var analyze = new NpgsqlCommand("ANALYZE social.posts; ANALYZE social.post_tags; ANALYZE social.follows; ANALYZE catalog.products;", connection);
                await analyze.ExecuteNonQueryAsync(cancellationToken);
            }

            return (viewer, CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
        }
    }
}
