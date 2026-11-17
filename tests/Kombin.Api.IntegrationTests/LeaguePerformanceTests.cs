using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.League;
using Kombin.Api.Features.League.Rules;
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
/// The day close (PR 3): at most <see cref="CloseBudget"/> at 10,000 players (section 13), measured at 1,000 and 10,000 players with the note's
/// shape (60 % of the players compete, every player answers 15 counted pairs, engine outfits at 5 % of the players).
/// </summary>
[Collection(LatencyBudgets.Name)]
public sealed class LeaguePerformanceTests(LeaguePerformanceTests.LeaguePerformanceFactory factory) : IClassFixture<LeaguePerformanceTests.LeaguePerformanceFactory>
{
    public const int Players = 5_000;

    public const int Engines = 1_000;

    public const int Voters = 12;

    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(30);

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

    [Theory(Skip = OutfitPerformanceTests.SkipMessage, SkipUnless = nameof(Enabled))]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task TheDayCloseStaysWithinThirtySecondsAtTenThousandPlayers(int players)
    {
        var day = new DateOnly(2041, 4, 1).AddDays(players / 1_000);
        var (entries, pairs) = await factory.SeedCloseDayAsync(day, players, Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var closer = scope.ServiceProvider.GetRequiredService<DayCloser>();
        var stopwatch = Stopwatch.StartNew();
        var result = await closer.CloseAsync(day, Time(day, 21, 0, 1), Ct);
        stopwatch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"close of {players} players ({entries} entries, {pairs} counted pairs): {stopwatch.Elapsed.TotalMilliseconds:F0} ms wall, close_ms {result.CloseMilliseconds}, "
            + $"{result.RankedCount} ranked, {result.ScoresWritten} score rows, Bradley–Terry {result.BtIterations} iterations (converged: {result.BtConverged})");

        Assert.Equal(DayCloseOutcome.Closed, result.Outcome);
        Assert.True(result.BtConverged);
        Assert.Equal(pairs, result.PairCount);
        Assert.Equal(players, result.ScoresWritten);
        Assert.True(stopwatch.Elapsed <= CloseBudget, $"close {stopwatch.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>The week close (task L5; design note section 13 estimates ~3 s at 10,000 players): every player in a group of about 30, seven closed days.</summary>
    [Theory(Skip = OutfitPerformanceTests.SkipMessage, SkipUnless = nameof(Enabled))]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task TheWeekCloseStaysWithinThirtySecondsAtTenThousandPlayers(int players)
    {
        var monday = new DateOnly(2041, 6, 10).AddDays(7 * (players / 1_000));
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        await factory.SeedWeekAsync(monday, players, closedDays: 7, [], Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var closer = scope.ServiceProvider.GetRequiredService<WeekCloser>();
        var stopwatch = Stopwatch.StartNew();
        var result = await closer.CloseWeekAsync(monday, Time(monday.AddDays(6), 21, 5), null, Ct);
        stopwatch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"week close of {players} players: {stopwatch.Elapsed.TotalMilliseconds:F0} ms wall, {result.Members} members, {result.Promoted} up, {result.Relegated} down, "
            + $"{result.NextGroups} groups of {result.NextMembers} formed, {result.StyleScores} style scores");

        Assert.Equal(WeekCloseOutcome.Closed, result.Outcome);
        Assert.Equal(players, result.Members);
        Assert.True(stopwatch.Elapsed <= CloseBudget, $"week close {stopwatch.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary><c>GET /league/table</c> with 10,000 players in groups of about 30 and three closed days: twelve players read their table 30 times each.</summary>
    [Fact(Skip = OutfitPerformanceTests.SkipMessage, SkipUnless = nameof(Enabled))]
    public async Task TheTableStaysWithinOneHundredMillisecondsAtP95()
    {
        var monday = new DateOnly(2041, 9, 9);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        var readers = new List<(HttpClient Client, Guid Id)>();
        for (var index = 0; index < Voters; index++)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            client.At(Time(monday.AddDays(-7), 9)); // joined the week before: the seed puts them in this week's groups
            await client.JoinLeagueOkAsync(Ct);
            readers.Add((client, (await client.GetJsonAsync("/api/v1/me", Ct)).GetProperty("id").GetGuid()));
        }

        await factory.SeedWeekAsync(monday, 10_000, closedDays: 3, [.. readers.Select(reader => reader.Id)], Ct);
        var samples = new List<double>();
        for (var round = 0; round < 30; round++)
        {
            foreach (var (client, _) in readers)
            {
                client.At(Time(monday.AddDays(3), 12, round));
                var stopwatch = Stopwatch.StartNew();
                using var response = await client.GetAsync("/api/v1/league/table", Ct);
                stopwatch.Stop();
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
                if (round > 0)
                {
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
            }
        }

        var p95 = Percentile(samples, 95);
        TestContext.Current.TestOutputHelper?.WriteLine($"league/table ({samples.Count} samples, 10,000 players): p50 {Percentile(samples, 50):F1} ms  p95 {p95:F1} ms  max {samples.Max():F1} ms");

        Assert.True(p95 <= Budget.TotalMilliseconds, $"league/table p95 {p95:F1} ms");
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

        private static readonly string[] BronzeOnly = ["Bronze"];

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

        /// <summary>
        /// A day to close: <paramref name="players"/> joined players, 60 % of them with a competing outfit, engine outfits at 5 % of the players,
        /// every player with 15 counted answers (distinct pairs, never their own outfit, a human on the left, the first answers at half weight)
        /// and their day counter. Returns the entries and the counted pairs.
        /// </summary>
        public async Task<(int Entries, int Pairs)> SeedCloseDayAsync(DateOnly day, int players, CancellationToken cancellationToken)
        {
            var random = new Random(players);
            var now = DateTimeOffset.UtcNow;
            var served = Time(day, 12);
            var playerIds = Enumerable.Range(0, players).Select(_ => Guid.CreateVersion7()).ToArray();
            var humans = (int)(players * 0.6);
            var engines = players / 20;
            var entryIds = Enumerable.Range(0, humans + engines).Select(_ => Guid.CreateVersion7()).ToArray();

            await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
            await connection.OpenAsync(cancellationToken);

            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY identity.users (id, email, password_hash, display_name, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                for (var index = 0; index < players; index++)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(playerIds[index], NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync($"closer-{day.DayNumber}-{index}@league.example.com", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync("not-a-real-hash", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync($"Oyuncu {index}", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY league.players (user_id, tier, joined_at, consent_version, counted_votes, idle_weeks, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var playerId in playerIds)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(playerId, NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync("Bronze", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(Time(day, 1), NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(LeagueConsent.Current.Version, NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(15, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync((short)0, NpgsqlDbType.Smallint, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY league.entries (id, day, user_id, kind, audience, occasion, piece_ids, piece_set_hash, status, exposures, comparisons, weighted_wins, weighted_games, counting_reports, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                cancellationToken))
            {
                for (var index = 0; index < entryIds.Length; index++)
                {
                    var human = index < humans;
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
                    await writer.WriteAsync(random.Next(2) == 0 ? "Women" : "Men", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync("Office", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }, NpgsqlDbType.Array | NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(Guid.NewGuid().ToString("N"), NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync("Competing", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync(0.0, NpgsqlDbType.Double, cancellationToken);
                    await writer.WriteAsync(0.0, NpgsqlDbType.Double, cancellationToken);
                    await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            // Hidden quality per outfit, so the votes have a structure for the fit to find (as real taste would).
            var quality = entryIds.Select(_ => (random.NextDouble() * 4) - 2).ToArray();
            var pairs = 0;
            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY league.pairs (id, day, voter_id, left_entry_id, right_entry_id, low_entry_id, high_entry_id, match_level, sequence, served_at, answered_at, choice, client_decision_ms, counted, weight) FROM STDIN (FORMAT BINARY)",
                cancellationToken))
            {
                for (var voter = 0; voter < players; voter++)
                {
                    var seen = new HashSet<(int, int)>();
                    for (var sequence = 1; sequence <= 15;)
                    {
                        var left = random.Next(humans);
                        var right = random.Next(entryIds.Length);
                        if (left == right || left == voter || right == voter || !seen.Add((Math.Min(left, right), Math.Max(left, right))))
                        {
                            continue;
                        }

                        var leftWins = random.NextDouble() < 1 / (1 + Math.Exp(quality[right] - quality[left]));
                        var (low, high) = entryIds[left].CompareTo(entryIds[right]) < 0 ? (entryIds[left], entryIds[right]) : (entryIds[right], entryIds[left]);
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(Guid.CreateVersion7(), NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(day, NpgsqlDbType.Date, cancellationToken);
                        await writer.WriteAsync(playerIds[voter], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(entryIds[left], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(entryIds[right], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(low, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(high, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync("Strict", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync((short)sequence, NpgsqlDbType.Smallint, cancellationToken);
                        await writer.WriteAsync(served.AddMinutes(sequence), NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(served.AddMinutes(sequence).AddSeconds(3), NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(leftWins ? "Left" : "Right", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(3000, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                        await writer.WriteAsync(sequence <= 5 ? 0.5f : 1f, NpgsqlDbType.Real, cancellationToken);
                        sequence++;
                        pairs++;
                    }
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync("COPY league.voter_days (user_id, day, served, counted) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var playerId in playerIds)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(playerId, NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(day, NpgsqlDbType.Date, cancellationToken);
                    await writer.WriteAsync(15, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync(15, NpgsqlDbType.Integer, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using var analyze = new NpgsqlCommand("ANALYZE league.entries; ANALYZE league.pairs; ANALYZE league.voter_days; ANALYZE league.players; ANALYZE identity.users;", connection);
            await analyze.ExecuteNonQueryAsync(cancellationToken);

            return (entryIds.Length, pairs);
        }

        /// <summary>
        /// A week of <paramref name="players"/> joined Bronze players in groups of about 30 (as the week close forms them, aliases by position),
        /// with a result row on each of the first <paramref name="closedDays"/> days (closed), random totals. <paramref name="placeFirst"/>: real
        /// players (already joined) put one into each of the first groups beside the synthetic ones.
        /// </summary>
        public async Task SeedWeekAsync(DateOnly monday, int players, int closedDays, IReadOnlyList<Guid> placeFirst, CancellationToken cancellationToken)
        {
            var random = new Random(players + closedDays);
            var now = DateTimeOffset.UtcNow;
            var playerIds = Enumerable.Range(0, players).Select(_ => Guid.CreateVersion7()).ToArray();

            await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
            await connection.OpenAsync(cancellationToken);

            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY identity.users (id, email, password_hash, display_name, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                for (var index = 0; index < players; index++)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(playerIds[index], NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync($"week-{monday.DayNumber}-{index}@league.example.com", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync("not-a-real-hash", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync($"Oyuncu {index}", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY league.players (user_id, tier, joined_at, consent_version, counted_votes, idle_weeks, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var playerId in playerIds)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(playerId, NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync("Bronze", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(Time(monday, 1), NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(LeagueConsent.Current.Version, NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(30, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync((short)0, NpgsqlDbType.Smallint, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using (var batch = new NpgsqlCommand(
                "INSERT INTO league.weeks (week_start, ends_at) VALUES (@monday, @ends) ON CONFLICT DO NOTHING;"
                + "INSERT INTO league.days (day, closes_at, closed_at, ranked_count, engine_count, pair_count, bt_iterations, bt_converged, close_ms) "
                + "SELECT d::date, now(), now(), 40, 0, 0, 0, true, 0 FROM generate_series(@monday::date, @last::date, interval '1 day') AS d ON CONFLICT (day) DO NOTHING;", connection))
            {
                batch.Parameters.AddWithValue("monday", monday);
                batch.Parameters.AddWithValue("last", monday.AddDays(closedDays - 1));
                batch.Parameters.AddWithValue("ends", Time(monday.AddDays(6), 21));
                await batch.ExecuteNonQueryAsync(cancellationToken);
            }

            var groups = (players + 29) / 30;
            var groupIds = Enumerable.Range(0, groups).Select(_ => Guid.CreateVersion7()).ToArray();
            await using (var writer = await connection.BeginBinaryImportAsync("COPY league.groups (id, week_start, tiers, capacity) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var groupId in groupIds)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(groupId, NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(monday, NpgsqlDbType.Date, cancellationToken);
                    await writer.WriteAsync(BronzeOnly, NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken);
                    await writer.WriteAsync(30, NpgsqlDbType.Integer, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY league.group_members (week_start, user_id, group_id, alias, tier, joined_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                async Task MemberAsync(Guid userId, int group, int position)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(monday, NpgsqlDbType.Date, cancellationToken);
                    await writer.WriteAsync(userId, NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(groupIds[group], NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(LeagueAliases.At(position), NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync("Bronze", NpgsqlDbType.Varchar, cancellationToken);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                for (var index = 0; index < players; index++)
                {
                    await MemberAsync(playerIds[index], index / 30, index % 30);
                }

                for (var index = 0; index < placeFirst.Count; index++)
                {
                    await MemberAsync(placeFirst[index], index, 30 + index);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY league.daily_scores (user_id, day, competed, ranked, rank, ranked_count, comparisons, percentile, vote_points, voting_points, worn_points, piece_points, total, under_review, removed_by_review) FROM STDIN (FORMAT BINARY)",
                cancellationToken))
            {
                for (var day = 0; day < closedDays; day++)
                {
                    for (var index = 0; index < players; index++)
                    {
                        var votes = random.Next(0, 71);
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(playerIds[index], NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync(monday.AddDays(day), NpgsqlDbType.Date, cancellationToken);
                        await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                        await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
                        await writer.WriteAsync(random.Next(1, 40), NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(40, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(12, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(votes / 70.0, NpgsqlDbType.Double, cancellationToken);
                        await writer.WriteAsync(votes, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(15, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(10, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(votes + 25, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync(false, NpgsqlDbType.Boolean, cancellationToken);
                        await writer.WriteAsync(false, NpgsqlDbType.Boolean, cancellationToken);
                    }
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await using var analyze = new NpgsqlCommand("ANALYZE league.daily_scores; ANALYZE league.group_members; ANALYZE league.groups; ANALYZE league.players; ANALYZE identity.users;", connection);
            await analyze.ExecuteNonQueryAsync(cancellationToken);
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

                // Every human entry belongs to a joined player (the materializer makes none otherwise), and pairing checks that the player has not left.
                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY league.players (user_id, tier, joined_at, consent_version, counted_votes, idle_weeks, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    foreach (var playerId in playerIds)
                    {
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(playerId, NpgsqlDbType.Uuid, cancellationToken);
                        await writer.WriteAsync("Bronze", NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                        await writer.WriteAsync(LeagueConsent.Current.Version, NpgsqlDbType.Varchar, cancellationToken);
                        await writer.WriteAsync(0, NpgsqlDbType.Integer, cancellationToken);
                        await writer.WriteAsync((short)0, NpgsqlDbType.Smallint, cancellationToken);
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

                await using var analyze = new NpgsqlCommand("ANALYZE league.entries; ANALYZE league.entry_pieces; ANALYZE identity.users; ANALYZE league.players;", connection);
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
