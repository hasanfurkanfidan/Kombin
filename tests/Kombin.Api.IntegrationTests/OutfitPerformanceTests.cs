using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
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
/// The outfit engine's latency budget: daily, how-to-wear, pairings and the wardrobe grid at most 300 ms p95 for a 200-piece wardrobe against a 50,000-product
/// catalog. The data is synthetic and seeded (<see cref="Random"/> with a fixed seed), so every run measures the same thing. It has its
/// own PostgreSQL container: 50,000 products in the shared test database would crowd the other tests' "newest products" queries.
/// A latency budget means something only on a quiet machine, so the tests run when <c>KOMBIN_PERF=1</c> is set: in CI, in a step of their
/// own. Without it they are skipped and the fixture starts nothing.
/// </summary>
public sealed class OutfitPerformanceTests(OutfitPerformanceTests.PerformanceFactory factory) : IClassFixture<OutfitPerformanceTests.PerformanceFactory>
{
    public const int Products = 50_000;

    public const int WardrobePieces = 200;

    public const int Samples = 60;

    public const string SkipMessage = "Set KOMBIN_PERF=1 to run the latency budget tests. They need a quiet machine; CI runs them in their own step.";

    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    public static bool Enabled => Environment.GetEnvironmentVariable("KOMBIN_PERF") == "1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = SkipMessage, SkipUnless = nameof(Enabled))]
    public async Task DailyAndHowToWearStayWithinThreeHundredMillisecondsAtP95()
    {
        var (client, anchors) = await factory.SeedAsync(Ct);

        // Warm-up: JIT, connection pool, query plans.
        for (var index = 0; index < 5; index++)
        {
            await TimeAsync(client, $"/api/v1/me/outfits/daily?seed={index}");
            await TimeAsync(client, $"/api/v1/me/outfits/how-to-wear?productId={anchors[index]}");
        }

        var daily = new List<double>();
        var howToWear = new List<double>();
        for (var index = 0; index < Samples; index++)
        {
            daily.Add(await TimeAsync(client, $"/api/v1/me/outfits/daily?date={(index % 2 == 0 ? "today" : "tomorrow")}&seed={100 + index}"));
            howToWear.Add(await TimeAsync(client, $"/api/v1/me/outfits/how-to-wear?productId={anchors[(index + 5) % anchors.Count]}&seed={index}"));
        }

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"daily       p50 {Percentile(daily, 50):F1} ms  p95 {Percentile(daily, 95):F1} ms  max {daily.Max():F1} ms");
        output?.WriteLine($"how-to-wear p50 {Percentile(howToWear, 50):F1} ms  p95 {Percentile(howToWear, 95):F1} ms  max {howToWear.Max():F1} ms");

        Assert.True(Percentile(daily, 95) <= Budget.TotalMilliseconds, $"daily p95 {Percentile(daily, 95):F1} ms");
        Assert.True(Percentile(howToWear, 95) <= Budget.TotalMilliseconds, $"how-to-wear p95 {Percentile(howToWear, 95):F1} ms");
    }

    [Fact(Skip = SkipMessage, SkipUnless = nameof(Enabled))]
    public async Task PairingsAndTheWardrobeGridStayWithinThreeHundredMillisecondsAtP95()
    {
        var (client, _) = await factory.SeedAsync(Ct);

        var (_, grid) = await MeasureAsync(client, "/api/v1/me/wardrobe/items");
        using var document = JsonDocument.Parse(grid);
        var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

        for (var index = 0; index < 5; index++)
        {
            await MeasureAsync(client, $"/api/v1/me/wardrobe/items/{items[index]}/pairings");
        }

        var pairings = new List<double>();
        var list = new List<double>();
        for (var index = 0; index < Samples; index++)
        {
            pairings.Add((await MeasureAsync(client, $"/api/v1/me/wardrobe/items/{items[index % items.Count]}/pairings")).Milliseconds);
            list.Add((await MeasureAsync(client, $"/api/v1/me/wardrobe/items?page={(index % 3) + 1}")).Milliseconds);
        }

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"pairings p50 {Percentile(pairings, 50):F1} ms  p95 {Percentile(pairings, 95):F1} ms  max {pairings.Max():F1} ms");
        output?.WriteLine($"grid     p50 {Percentile(list, 50):F1} ms  p95 {Percentile(list, 95):F1} ms  max {list.Max():F1} ms");

        Assert.True(Percentile(pairings, 95) <= Budget.TotalMilliseconds, $"pairings p95 {Percentile(pairings, 95):F1} ms");
        Assert.True(Percentile(list, 95) <= Budget.TotalMilliseconds, $"grid p95 {Percentile(list, 95):F1} ms");
    }

    private static async Task<double> TimeAsync(HttpClient client, string url)
    {
        var (milliseconds, body) = await MeasureAsync(client, url);

        using var json = JsonDocument.Parse(body);
        var outfits = json.RootElement.TryGetProperty("outfits", out var list) ? list.GetArrayLength() : json.RootElement.GetProperty("outfit").ValueKind == JsonValueKind.Object ? 1 : 0;
        Assert.True(outfits > 0, $"{url} returned no outfit");

        return milliseconds;
    }

    private static async Task<(double Milliseconds, string Body)> MeasureAsync(HttpClient client, string url)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        stopwatch.Stop();

        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {body}");

        return (stopwatch.Elapsed.TotalMilliseconds, body);
    }

    private static double Percentile(List<double> samples, int percentile)
    {
        var sorted = samples.Order().ToList();
        return sorted[Math.Max(0, (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1)];
    }

    /// <summary>A second API host on its own database, configured like <see cref="ApiFactory"/>.</summary>
    public sealed class PerformanceFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg18").Build();

        private static readonly string[] Sizes = ["S", "M", "L"];

        private readonly SemaphoreSlim _seedLock = new(1, 1);

        private (HttpClient Client, List<Guid> Anchors)? _seeded;

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

        /// <summary>Seeds once: the catalog by binary COPY, then a user with a 200-piece wardrobe. Returns the user's client and some anchors.</summary>
        public async Task<(HttpClient Client, List<Guid> Anchors)> SeedAsync(CancellationToken cancellationToken)
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

        private async Task<(HttpClient Client, List<Guid> Anchors)> SeedOnceAsync(CancellationToken cancellationToken)
        {
            var random = new Random(20261102);
            var now = DateTimeOffset.UtcNow;
            var colours = Enum.GetValues<ProductColor>();
            var fits = new[] { ProductFit.Slim, ProductFit.Regular, ProductFit.Relaxed, ProductFit.Oversized };
            var patterns = new[] { ProductPattern.Solid, ProductPattern.Solid, ProductPattern.Solid, ProductPattern.Striped, ProductPattern.Checked, ProductPattern.Printed, ProductPattern.Textured };
            var audiences = new[] { Audience.Women, Audience.Men, Audience.Unisex };

            Dictionary<string, Guid> categories;
            Guid brandId;
            Guid sellerId;

            await using (var scope = Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
                categories = await db.Categories.Where(category => category.ParentId != null).ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);

                var brand = new Brand { Name = "Perf Brand", Slug = "perf-brand" };
                var seller = new Seller { Name = "Perf Seller", Slug = "perf-seller", Kind = SellerKind.Marketplace, WebsiteUrl = "https://perf.example.com" };
                db.AddRange(brand, seller);
                await db.SaveChangesAsync(cancellationToken);
                (brandId, sellerId) = (brand.Id, seller.Id);
            }

            var leaves = categories.Keys.Order(StringComparer.Ordinal).ToArray();
            var productIds = new Guid[Products];

            await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
            {
                await connection.OpenAsync(cancellationToken);

                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY catalog.products (id, brand_id, category_id, name, slug, audience, primary_color, pattern, fit, formality, seasons, is_active, primary_image_url, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                    cancellationToken))
                {
                    for (var index = 0; index < Products; index++)
                    {
                        var id = Guid.CreateVersion7();
                        productIds[index] = id;
                        var leaf = leaves[random.Next(leaves.Length)];

                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(brandId, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(categories[leaf], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync($"Perf {leaf} {index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync($"perf-{index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(audiences[random.Next(audiences.Length)].ToString(), NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(colours[random.Next(colours.Length)].ToString(), NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(patterns[random.Next(patterns.Length)].ToString(), NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(fits[random.Next(fits.Length)].ToString(), NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync((short)random.Next(1, 6), NpgsqlDbType.Smallint, cancellationToken);
                        await writer.WriteAsync((short)Season.AllYear, NpgsqlDbType.Smallint, cancellationToken);
                        await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                        await writer.WriteAsync($"https://img.example.com/perf/{index}.jpg", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(now.AddSeconds(-index), NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(now.AddSeconds(-index), NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY catalog.offers (id, product_id, seller_id, external_id, url, title, current_price, currency, shipping_cost, stock_status, available_sizes, last_seen_at, is_active, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                    cancellationToken))
                {
                    for (var index = 0; index < Products; index++)
                    {
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(Guid.CreateVersion7(), NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(productIds[index], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(sellerId, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync($"perf-{index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync($"https://perf.example.com/p/{index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync($"Perf {index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync((decimal)random.Next(200, 5000), NpgsqlDbType.Numeric, cancellationToken);
                        await writer.WriteAsync("TRY", NpgsqlDbType.Char, cancellationToken);
                        await writer.WriteAsync(random.Next(2) == 0 ? 0m : 39.9m, NpgsqlDbType.Numeric, cancellationToken);
                        await writer.WriteAsync(StockStatus.InStock.ToString(), NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(Sizes, NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using var analyze = new NpgsqlCommand("ANALYZE catalog.products; ANALYZE catalog.offers;", connection);
                await analyze.ExecuteNonQueryAsync(cancellationToken);
            }

            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var tokens = await client.RegisterAndReadAsync(cancellationToken: cancellationToken);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            var userId = (await client.GetJsonAsync("/api/v1/me", cancellationToken)).GetProperty("id").GetGuid();
            await client.SetCityAsync("Istanbul", cancellationToken);

            // 200 pieces spread like a real wardrobe: tops and bottoms first, then shoes, layers and a few accessories.
            var groups = new (string Root, int Share)[] { ("ust-giyim", 35), ("alt-giyim", 25), ("ayakkabi", 15), ("dis-giyim", 15), ("aksesuar", 10) };
            await using (var scope = Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
                foreach (var (root, share) in groups)
                {
                    var rootLeaves = leaves.Where(leaf => leaf.StartsWith(root + "/", StringComparison.Ordinal)).ToArray();
                    for (var index = 0; index < WardrobePieces * share / 100; index++)
                    {
                        db.WardrobeItems.Add(new WardrobeItem
                        {
                            UserId = userId,
                            CategoryId = categories[rootLeaves[random.Next(rootLeaves.Length)]],
                            PrimaryColor = colours[random.Next(colours.Length)],
                            Pattern = patterns[random.Next(patterns.Length)],
                            Fit = fits[random.Next(fits.Length)],
                            Formality = (short)random.Next(1, 6),
                            Seasons = Season.AllYear,
                            Status = WardrobeItemStatus.Ready,
                        });
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            // Anchors: a spread of catalog products, the same ones every run.
            var anchors = Enumerable.Range(0, 20).Select(index => productIds[(index * 2477) % Products]).ToList();

            return (client, anchors);
        }
    }
}
