using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe;
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
using Pgvector;
using Pgvector.Npgsql;
using Testcontainers.PostgreSql;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Visual search's latency budget: <c>POST /lens/search</c> for one region at most 400 ms p95 with 50,000 embedded products, excluding the
/// vision call (the vision service is a fake that answers at once, so what is measured is everything of ours: the upload, the HNSW search with
/// its family and audience filters, offers, cards and, signed in, the profile audience and 200 wardrobe pieces). The vectors are clustered like
/// real CLIP embeddings (500 looks of 100 products, one category each), so every search has a full rail of 12 results above the floor.
/// Synthetic data on its own PostgreSQL container, gated by <c>KOMBIN_PERF=1</c> like <see cref="FeedPerformanceTests"/>.
/// </summary>
public sealed class LensPerformanceTests(LensPerformanceTests.LensPerformanceFactory factory) : IClassFixture<LensPerformanceTests.LensPerformanceFactory>
{
    public const int Products = 50_000;

    public const int Looks = 500;

    public const int WardrobePieces = 200;

    public const int Samples = 60;

    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static bool Enabled => OutfitPerformanceTests.Enabled;

    [Fact(Skip = OutfitPerformanceTests.SkipMessage, SkipUnless = nameof(Enabled))]
    public async Task OneRegionStaysWithinFourHundredMillisecondsAtP95()
    {
        var (viewer, anonymous) = await factory.SeedAsync(Ct);
        var random = new Random(7);

        for (var index = 0; index < 5; index++)
        {
            await MeasureAsync(viewer, factory.QueryPhoto(random));
            await MeasureAsync(anonymous, factory.QueryPhoto(random));
        }

        var signedIn = new List<double>();
        var signedOut = new List<double>();
        for (var index = 0; index < Samples; index++)
        {
            signedIn.Add(await MeasureAsync(viewer, factory.QueryPhoto(random)));
            signedOut.Add(await MeasureAsync(anonymous, factory.QueryPhoto(random)));
        }

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"HNSW build over {Products} vectors: {factory.IndexBuild.TotalSeconds:F1} s");
        output?.WriteLine(await factory.ExplainAsync(random, Ct));
        output?.WriteLine($"signed in  p50 {Percentile(signedIn, 50):F1} ms  p95 {Percentile(signedIn, 95):F1} ms  max {signedIn.Max():F1} ms");
        output?.WriteLine($"anonymous  p50 {Percentile(signedOut, 50):F1} ms  p95 {Percentile(signedOut, 95):F1} ms  max {signedOut.Max():F1} ms");

        Assert.True(Percentile(signedIn, 95) <= Budget.TotalMilliseconds, $"signed in p95 {Percentile(signedIn, 95):F1} ms");
        Assert.True(Percentile(signedOut, 95) <= Budget.TotalMilliseconds, $"anonymous p95 {Percentile(signedOut, 95):F1} ms");
    }

    private static async Task<double> MeasureAsync(HttpClient client, byte[] photo)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "image", "photo.jpg");

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.PostAsync("/api/v1/lens/search", content, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        stopwatch.Stop();

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("regions")[0].GetProperty("results").GetArrayLength() >= 6, "the search found fewer than 6 products");

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(List<double> samples, int percentile)
    {
        var sorted = samples.Order().ToList();
        return sorted[Math.Max(0, (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1)];
    }

    private static float[] Normalize(float[] vector)
    {
        var length = (float)Math.Sqrt(vector.Sum(value => (double)value * value));
        return [.. vector.Select(value => value / length)];
    }

    private static float[] Gaussian(Random random)
    {
        var vector = new float[512];
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()));
        }

        return Normalize(vector);
    }

    /// <summary><paramref name="weight"/> of noise on a unit vector: two members of a look at 0.5 are ~0.8 alike.</summary>
    private static float[] Near(float[] center, Random random, float weight)
    {
        var noise = Gaussian(random);
        return Normalize([.. center.Select((value, index) => value + (weight * noise[index]))]);
    }

    /// <summary>The vision service for the measurement: a photo's text names the region (category and vector) it answers, at once.</summary>
    private sealed class PerfVisionClient : IVisionClient
    {
        public const string Model = "ViT-B-32/perf";

        public static readonly ConcurrentDictionary<string, (string Slug, float[] Embedding)> Photos = new(StringComparer.Ordinal);

        public Task<VisionCall<VisionDetection>> DetectAsync(byte[] image, string contentType, bool embed, CancellationToken cancellationToken)
        {
            var (slug, embedding) = Photos[Encoding.UTF8.GetString(image)];
            var category = new VisionCategory(slug, 0.8);

            return Task.FromResult(VisionCall<VisionDetection>.Ok(new VisionDetection(
                "flatlay",
                [new VisionRegion(new VisionRegionBox(0.2, 0.2, 0.6, 0.6), "Top", category, [category], new VisionColor("Navy", 0.7, null), 0.8, embedding)],
                embedding)));
        }

        public Task<string?> GetEmbeddingModelAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Model);

        public Task<VisionResult> AnalyzeAsync(byte[] image, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VisionCall<VisionModeration>> ModerateAsync(byte[] image, string contentType, bool renditions, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VisionCall<VisionEmbedding>> EmbedAsync(byte[] image, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A fourth API host on its own database, configured like <see cref="ApiFactory"/>.</summary>
    public sealed class LensPerformanceFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg18").Build();

        private readonly SemaphoreSlim _seedLock = new(1, 1);

        private (HttpClient Viewer, HttpClient Anonymous)? _seeded;

        private static readonly string[] WomenAndUnisex = ["Women", "Unisex"];

        private (string Slug, float[] Center)[] _looks = [];

        /// <summary>How long the HNSW index took to build after the bulk load, for the design doc.</summary>
        public TimeSpan IndexBuild { get; private set; }

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

        /// <summary>A photo of a random garment of a random look: its vector is a little off the look's centre, as a street photo would be.</summary>
        internal byte[] QueryPhoto(Random random)
        {
            var (slug, center) = _looks[random.Next(_looks.Length)];
            var key = $"perf-photo-{Guid.NewGuid():N}";
            PerfVisionClient.Photos[key] = (slug, Near(center, random, 0.35f));

            return Encoding.UTF8.GetBytes(key);
        }

        /// <summary>
        /// The plans of the catalog search as the endpoint runs it (same settings, filters and order): a family-restricted region (every
        /// category under the look's root) and an unrestricted whole photo. The vector literal is cut out of the plan text.
        /// </summary>
        internal async Task<string> ExplainAsync(Random random, CancellationToken cancellationToken)
        {
            var (slug, center) = _looks[random.Next(_looks.Length)];
            var builder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
            builder.UseVector();
            await using var dataSource = builder.Build();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var tune = new NpgsqlCommand("SET LOCAL hnsw.ef_search = 100; SET LOCAL hnsw.iterative_scan = relaxed_order;", connection, transaction))
            {
                await tune.ExecuteNonQueryAsync(cancellationToken);
            }

            var lines = new List<string>();
            foreach (var (name, filter) in new[]
            {
                ("family", "AND p.category_id IN (SELECT id FROM catalog.categories WHERE slug LIKE @root || '%')"),
                ("whole photo", string.Empty),
            })
            {
                await using var explain = new NpgsqlCommand(
                    $"""
                    EXPLAIN ANALYZE
                    SELECT e.product_id, e.embedding <=> @query AS distance
                    FROM catalog.product_embeddings e JOIN catalog.products p ON p.id = e.product_id
                    WHERE e.embedding IS NOT NULL AND e.model = @model AND p.is_active AND p.audience = ANY (@audiences) {filter}
                    ORDER BY e.embedding <=> @query
                    LIMIT 48
                    """,
                    connection,
                    transaction);
                explain.Parameters.AddWithValue("query", new Vector(Near(center, random, 0.35f)));
                explain.Parameters.AddWithValue("model", PerfVisionClient.Model);
                explain.Parameters.AddWithValue("root", slug.Split('/')[0]);
                explain.Parameters.AddWithValue("audiences", WomenAndUnisex);

                lines.Add($"--- {name}");
                await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var line = reader.GetString(0);
                    var literal = line.IndexOf("'[", StringComparison.Ordinal);
                    lines.Add(literal < 0 ? line : line[..literal] + "<vector>");
                }
            }

            return string.Join(Environment.NewLine, lines);
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
                services.RemoveAll<IVisionClient>();
                services.AddSingleton<IVisionClient, PerfVisionClient>();
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
            var random = new Random(20261108);
            var now = DateTimeOffset.UtcNow;
            var audiences = new[] { Audience.Women, Audience.Men, Audience.Unisex };

            Dictionary<string, Guid> categories;
            Guid brandId;
            Guid sellerId;
            await using (var scope = Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
                categories = await db.Categories.Where(category => category.ParentId != null).ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);

                var brand = new Brand { Name = "Lens Brand", Slug = "lens-brand" };
                var seller = new Seller { Name = "Lens Seller", Slug = "lens-seller", Kind = SellerKind.Marketplace, WebsiteUrl = "https://lens.example.com" };
                db.AddRange(brand, seller);
                await db.SaveChangesAsync(cancellationToken);
                (brandId, sellerId) = (brand.Id, seller.Id);
            }

            var leaves = categories.Keys.Order(StringComparer.Ordinal).ToArray();
            _looks = [.. Enumerable.Range(0, Looks).Select(index => (leaves[index % leaves.Length], Gaussian(random)))];

            var builder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
            builder.UseVector();
            await using var dataSource = builder.Build();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

            // Bulk-loading into an HNSW index inserts row by row; building it once afterwards is the realistic (and much faster) path.
            string indexDefinition;
            await using (var definition = new NpgsqlCommand("SELECT pg_get_indexdef('catalog.ix_product_embeddings_embedding_hnsw'::regclass)", connection))
            {
                indexDefinition = (string)(await definition.ExecuteScalarAsync(cancellationToken))!;
            }

            await using (var drop = new NpgsqlCommand("DROP INDEX catalog.ix_product_embeddings_embedding_hnsw", connection))
            {
                await drop.ExecuteNonQueryAsync(cancellationToken);
            }

            var productIds = new Guid[Products];
            var lookOf = new int[Products];
            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY catalog.products (id, brand_id, category_id, name, slug, audience, primary_color, pattern, fit, formality, seasons, is_active, primary_image_url, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                cancellationToken))
            {
                for (var index = 0; index < Products; index++)
                {
                    productIds[index] = Guid.CreateVersion7();
                    lookOf[index] = index % Looks;

                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(productIds[index], NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(brandId, NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(categories[_looks[lookOf[index]].Slug], NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync($"Lens {index}", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync($"lens-{index}", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(audiences[random.Next(audiences.Length)].ToString(), NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(nameof(ProductColor.Navy), NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(nameof(ProductPattern.Solid), NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(nameof(ProductFit.Regular), NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync((short)random.Next(1, 6), NpgsqlDbType.Smallint, cancellationToken);
                    await writer.WriteAsync((short)Season.AllYear, NpgsqlDbType.Smallint, cancellationToken);
                    await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                    await writer.WriteAsync($"https://img.example.com/lens/{index}.jpg", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            string[] sizes = ["S", "M", "L"];
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
                    await writer.WriteAsync($"lens-{index}", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync($"https://lens.example.com/p/{index}", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync($"Lens {index}", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync((decimal)random.Next(200, 5000), NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync("TRY", NpgsqlDbType.Char, cancellationToken);
                    await writer.WriteAsync(0m, NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync((random.Next(10) == 0 ? StockStatus.OutOfStock : StockStatus.InStock).ToString(), NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(sizes, NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY catalog.product_embeddings (product_id, embedding, model, source_image_url, status, attempts, checked_at, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                cancellationToken))
            {
                for (var index = 0; index < Products; index++)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(productIds[index], NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(new Vector(Near(_looks[lookOf[index]].Center, random, 0.5f)), "vector", cancellationToken);
                    await writer.WriteAsync(PerfVisionClient.Model, NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync($"https://img.example.com/lens/{index}.jpg", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(nameof(ProductEmbeddingStatus.Ready), NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            // The container's /dev/shm is small, so the build runs without parallel workers; the graph (~110 MB) fits in maintenance_work_mem.
            await using (var build = new NpgsqlCommand(
                $"SET maintenance_work_mem = '512MB'; SET max_parallel_maintenance_workers = 0; {indexDefinition}; ANALYZE catalog.products; ANALYZE catalog.offers; ANALYZE catalog.product_embeddings;",
                connection))
            {
                build.CommandTimeout = 900;
                var stopwatch = Stopwatch.StartNew();
                await build.ExecuteNonQueryAsync(cancellationToken);
                IndexBuild = stopwatch.Elapsed;
            }

            var anonymous = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var viewer = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var tokens = await viewer.RegisterAndReadAsync(cancellationToken: cancellationToken);
            viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            var userId = (await viewer.GetJsonAsync("/api/v1/me", cancellationToken)).GetProperty("id").GetGuid();
            using (var profile = await viewer.PutAsJsonAsync("/api/v1/me/profile", new { audience = "Women" }, cancellationToken))
            {
                profile.EnsureSuccessStatusCode();
            }

            await using (var scope = Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
                for (var index = 0; index < WardrobePieces; index++)
                {
                    var (slug, center) = _looks[random.Next(_looks.Length)];
                    db.WardrobeItems.Add(new WardrobeItem
                    {
                        UserId = userId,
                        CategoryId = categories[slug],
                        PrimaryColor = ProductColor.Navy,
                        Status = WardrobeItemStatus.Ready,
                        Embedding = new Vector(Near(center, random, 0.5f)),
                        EmbeddingModel = PerfVisionClient.Model,
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            return (viewer, anonymous);
        }
    }
}
