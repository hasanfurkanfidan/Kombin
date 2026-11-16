using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Recommendation.Weather;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Kombin Ligi's pairing budget (<c>docs/design/league.md</c>, section 13): <c>GET /league/pairs/next</c> at most 100 ms p95 with 6,000 entries
/// in the day's pool (5,000 players' outfits over three audiences and six occasion buckets, 1,000 engine outfits, exposures already spread),
/// measured while 12 voters each serve and answer their 30 pairs of the day, so the candidate query, the voter's served pairs, the choice and the
/// serving transaction are all on the path. Synthetic data on its own PostgreSQL container, gated by <c>KOMBIN_PERF=1</c> like the other budgets.
/// The day close's budget comes with the close (PR 3).
/// </summary>
[Collection(LatencyBudgets.Name)]
public sealed class LeaguePerformanceTests(LeaguePerformanceTests.LeaguePerformanceFactory factory) : IClassFixture<LeaguePerformanceTests.LeaguePerformanceFactory>
{
    public const int Players = 5_000;

    public const int Engines = 1_000;

    public const int Voters = 12;

    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static bool Enabled => OutfitPerformanceTests.Enabled;

    [Fact(Skip = OutfitPerformanceTests.SkipMessage, SkipUnless = nameof(Enabled))]
    public async Task TheNextPairStaysWithinOneHundredMillisecondsAtP95()
    {
        var (day, voters) = await factory.SeedAsync(Ct);
        var samples = new List<double>();

        for (var round = 0; round < 30; round++)
        {
            foreach (var voter in voters)
            {
                voter.At(Time(day, 12, round, 0));
                var stopwatch = Stopwatch.StartNew();
                using var response = await voter.GetAsync(NextPair, Ct);
                var body = await response.Content.ReadAsStringAsync(Ct);
                stopwatch.Stop();

                Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
                var pair = JsonDocument.Parse(body).RootElement.Pair();
                if (round > 0)
                {
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }

                voter.At(Time(day, 12, round, 2));
                await voter.VoteOkAsync(pair.GetProperty("id").GetGuid(), round % 4 == 0 ? "skip" : "left", cancellationToken: Ct);
            }
        }

        var p95 = Percentile(samples, 95);
        TestContext.Current.TestOutputHelper?.WriteLine($"pairs/next ({samples.Count} samples, {Players + Engines} entries): p50 {Percentile(samples, 50):F1} ms  p95 {p95:F1} ms  max {samples.Max():F1} ms");

        Assert.True(p95 <= Budget.TotalMilliseconds, $"pairs/next p95 {p95:F1} ms");
    }

    private static double Percentile(List<double> samples, int percentile)
    {
        var sorted = samples.Order().ToList();
        return sorted[Math.Max(0, (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1)];
    }

    /// <summary>An API host on its own database, configured like <see cref="ApiFactory"/>, with the league on and the test clock.</summary>
    public sealed class LeaguePerformanceFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg18").Build();

        private readonly SemaphoreSlim _seedLock = new(1, 1);

        private (DateOnly Day, IReadOnlyList<HttpClient> Voters)? _seeded;

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

        public async Task<(DateOnly Day, IReadOnlyList<HttpClient> Voters)> SeedAsync(CancellationToken cancellationToken)
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
                services.AddHttpContextAccessor();
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock, TestHeaderClock>();
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
                ["RateLimits:Enabled"] = "false",
                ["League:Enabled"] = "true",
            }));
        }

        private async Task<(DateOnly Day, IReadOnlyList<HttpClient> Voters)> SeedOnceAsync(CancellationToken cancellationToken)
        {
            var random = new Random(20261116);
            var day = new DateOnly(2041, 3, 14);
            var now = DateTimeOffset.UtcNow;
            string[] audiences = ["Women", "Men", "Unisex"];
            string?[] occasions = [null, "Office", "Weekend", "Evening", "Sport", "Event"];
            string[] slots = ["Top", "Bottom", "Shoes"];

            await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
            {
                await connection.OpenAsync(cancellationToken);

                var playerIds = new Guid[Players];
                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY identity.users (id, email, password_hash, display_name, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    for (var index = 0; index < Players; index++)
                    {
                        playerIds[index] = Guid.CreateVersion7();
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(playerIds[index], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync($"player-{index}@league.example.com", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync("not-a-real-hash", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync($"Oyuncu {index}", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                var entryIds = new Guid[Players + Engines];
                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY league.entries (id, day, user_id, kind, audience, occasion, piece_ids, piece_set_hash, status, exposures, comparisons, weighted_wins, weighted_games, counting_reports, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                    cancellationToken))
                {
                    for (var index = 0; index < entryIds.Length; index++)
                    {
                        var human = index < Players;
                        entryIds[index] = Guid.CreateVersion7();
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(entryIds[index], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(day, NpgsqlDbType.Date, cancellationToken);
                        if (human)
                        {
                            await writer.WriteAsync(playerIds[index], NpgsqlDbType.Uuid, cancellationToken);
                        }
                        else
                        {
                            await writer.WriteNullAsync(cancellationToken);
                        }

                        await writer.WriteAsync(human ? "Human" : "Engine", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(audiences[random.Next(audiences.Length)], NpgsqlDbType.Varchar, cancellationToken);
                        if (occasions[random.Next(occasions.Length)] is { } occasion)
                        {
                            await writer.WriteAsync(occasion, NpgsqlDbType.Varchar, cancellationToken);
                        }
                        else
                        {
                            await writer.WriteNullAsync(cancellationToken);
                        }

                        await writer.WriteAsync(new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }, NpgsqlDbType.Array | NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(Guid.NewGuid().ToString("N"), NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync("Competing", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(random.Next(0, 25), NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(0.0, NpgsqlDbType.Double, cancellationToken);
                        await writer.WriteAsync(0.0, NpgsqlDbType.Double, cancellationToken);
                        await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY league.entry_pieces (entry_id, position, slot, image_key, image_url) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    for (var index = 0; index < entryIds.Length; index++)
                    {
                        for (var position = 0; position < slots.Length; position++)
                        {
                            await writer.StartRowAsync(cancellationToken);
                            await writer.WriteAsync(entryIds[index], NpgsqlDbType.Uuid, cancellationToken);
                            await writer.WriteAsync((short)position, NpgsqlDbType.Smallint, cancellationToken);
                            await writer.WriteAsync(slots[position], NpgsqlDbType.Varchar, cancellationToken);
                            if (index < Players)
                            {
                                await writer.WriteAsync($"league/{Guid.NewGuid():D}/{Guid.NewGuid():D}/thumb.webp", NpgsqlDbType.Varchar, cancellationToken);
                                await writer.WriteNullAsync(cancellationToken);
                            }
                            else
                            {
                                await writer.WriteNullAsync(cancellationToken);
                                await writer.WriteAsync($"https://img.example.com/engine/{Guid.NewGuid():N}.jpg", NpgsqlDbType.Varchar, cancellationToken);
                            }
                        }
                    }

                    await writer.CompleteAsync(cancellationToken);
                }

                await using var analyze = new NpgsqlCommand("ANALYZE league.entries; ANALYZE league.entry_pieces; ANALYZE identity.users;", connection);
                await analyze.ExecuteNonQueryAsync(cancellationToken);
            }

            var voters = new List<HttpClient>();
            for (var index = 0; index < Voters; index++)
            {
                var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                var tokens = await client.RegisterAndReadAsync(cancellationToken: cancellationToken);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                client.DefaultRequestHeaders.Add(TestHeaderClock.Header, Time(day, 9).ToString("O", CultureInfo.InvariantCulture));
                await client.JoinLeagueOkAsync(cancellationToken);
                voters.Add(client);
            }

            return (day, voters);
        }
    }
}
