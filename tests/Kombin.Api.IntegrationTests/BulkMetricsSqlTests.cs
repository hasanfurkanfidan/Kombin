using System.Text.RegularExpressions;
using Kombin.Api.Features.Admin.Pages;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The stop rule of bulk add (<c>-- bulk:</c> blocks of <c>docs/metrics.sql</c>, design note section f) is run by hand, so nothing else would notice a broken
/// one: every block runs against the real schema with the psql parameters bound, and answers the counters of seeded batches. Each test seeds its own
/// day of 2019, so the shared database never adds other tests' batches to its numbers. The admin page reads the same numbers.
/// </summary>
public sealed partial class BulkMetricsSqlTests(ApiFactory factory)
{
    private static readonly string[] Expected = ["bulk_counters", "bulk_refusals"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryBulkMetricBlockRunsAgainstTheSchema()
    {
        var blocks = BulkBlocks(await File.ReadAllTextAsync(MetricsFile(), Ct));
        Assert.Equal(Expected, blocks.Keys);

        await using var connection = await factory.OpenConnectionAsync(Ct);
        foreach (var (name, sql) in blocks)
        {
            await using var reader = await Run(connection, sql, new DateOnly(2020, 1, 1), new DateOnly(2041, 1, 1));
            await reader.ReadAsync(Ct);
        }
    }

    [Fact]
    public async Task CountersAreTheSumsOfTheConfirmedBatches()
    {
        await SeedAsync(day: 2);

        await using var connection = await factory.OpenConnectionAsync(Ct);
        await using var reader = await Run(connection, BulkBlocks(await File.ReadAllTextAsync(MetricsFile(), Ct))["bulk_counters"], new DateOnly(2019, 3, 2), new DateOnly(2019, 3, 3));

        Assert.True(await reader.ReadAsync(Ct));
        var row = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, index => Convert.ToDecimal(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(await reader.ReadAsync(Ct));
        Assert.Equal(
            new Dictionary<string, decimal>
            {
                ["batches_processed"] = 6,
                ["batches_confirmed"] = 2,
                ["pieces_shown"] = 8,
                ["pieces_pre_ticked"] = 6,
                ["pieces_ticked"] = 6,
                ["unticked_pre_ticks"] = 1,
                ["ticked_extras"] = 1,
                ["edits_per_ticked_piece"] = 0.5m,
                ["median_seconds_to_ready"] = 8.0m,
            },
            row);
    }

    [Fact]
    public async Task AnEmptyWindowGivesZeroCountsAndNoMedian()
    {
        await using var connection = await factory.OpenConnectionAsync(Ct);
        await using var reader = await Run(connection, BulkBlocks(await File.ReadAllTextAsync(MetricsFile(), Ct))["bulk_counters"], new DateOnly(2019, 2, 1), new DateOnly(2019, 2, 2));

        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(0L, reader.GetInt64(reader.GetOrdinal("batches_confirmed")));
        Assert.True(reader.IsDBNull(reader.GetOrdinal("edits_per_ticked_piece")));
        Assert.True(reader.IsDBNull(reader.GetOrdinal("median_seconds_to_ready")));
    }

    [Fact]
    public async Task RefusalsAreCountedByCodeMostFrequentFirst()
    {
        await SeedAsync(day: 3);

        await using var connection = await factory.OpenConnectionAsync(Ct);
        await using var reader = await Run(connection, BulkBlocks(await File.ReadAllTextAsync(MetricsFile(), Ct))["bulk_refusals"], new DateOnly(2019, 3, 3), new DateOnly(2019, 3, 4));

        var rows = new List<(string Code, long Batches)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        Assert.Equal([("closet", 2L), ("rail", 1L)], rows);
    }

    [Fact]
    public async Task AdminPageReadsConfirmedBatchCounters()
    {
        await SeedAsync(day: 4);

        var (numbers, refusals) = await factory.WithDbAsync(db => BulkModel.ReadAsync(db, At(4).AddHours(-12), At(4).AddHours(12), Ct));

        Assert.Equal(new BulkNumbers(6, 2, 8, 6, 6, 1, 1, 0.5m, 8.0), numbers);
        Assert.Equal([KeyValuePair.Create("closet", 2), KeyValuePair.Create("rail", 1)], refusals);
    }

    [Fact]
    public async Task AdminPageOmitsTheUserId()
    {
        var userId = await SeedAsync(day: 5);
        using var browser = await factory.SignedInAdminAsync(Ct);

        var html = await browser.GetHtmlAsync("/admin/bulk", Ct);

        Assert.Contains("Refusals by reason", html, StringComparison.Ordinal);
        Assert.Contains("Median seconds to Ready", html, StringComparison.Ordinal);
        Assert.DoesNotContain(userId.ToString(), html, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset At(int day) => new(2019, 3, day, 12, 0, 0, TimeSpan.Zero);

    private static async Task<NpgsqlDataReader> Run(NpgsqlConnection connection, string sql, DateOnly start, DateOnly end)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("day_start", start);
        command.Parameters.AddWithValue("day_end", end);

        return await command.ExecuteReaderAsync(Ct);
    }

    /// <summary>Two confirmed batches (5 shown, 4 pre-ticked, 4 ticked, 3 of them pre-ticked, 3 edits; 3 shown, 2 pre-ticked, all ticked), three refused (closet twice, rail), one cancelled with counters that must not count.</summary>
    private async Task<Guid> SeedAsync(int day)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var userId = user.Id;
        var batches = new List<BulkBatch>
        {
            Batch(userId, BulkBatchStatus.Confirmed, shown: 5, preTicked: 4, ticked: 4, tickedPreTicked: 3, categoryEdits: 2, colourEdits: 1, visionMs: 4000),
            Batch(userId, BulkBatchStatus.Confirmed, shown: 3, preTicked: 2, ticked: 2, tickedPreTicked: 2, visionMs: 8000),
            Batch(userId, BulkBatchStatus.Refused, refusal: "closet"),
            Batch(userId, BulkBatchStatus.Refused, refusal: "closet"),
            Batch(userId, BulkBatchStatus.Refused, refusal: "rail", visionMs: 99999),
            Batch(userId, BulkBatchStatus.Cancelled, shown: 9, preTicked: 9, ticked: 9, tickedPreTicked: 9, categoryEdits: 5, visionMs: 20000),
        };

        await factory.WithDbAsync(async db =>
        {
            db.BulkBatches.AddRange(batches);
            await db.SaveChangesAsync(Ct);
            var ids = batches.Select(batch => batch.Id).ToList();
            await db.BulkBatches.Where(batch => ids.Contains(batch.Id)).ExecuteUpdateAsync(set => set.SetProperty(batch => batch.CreatedAt, At(day)), Ct);

            return 0;
        });

        return userId;
    }

    private static BulkBatch Batch(
        Guid userId,
        BulkBatchStatus status,
        int shown = 0,
        int preTicked = 0,
        int ticked = 0,
        int tickedPreTicked = 0,
        int categoryEdits = 0,
        int colourEdits = 0,
        int? visionMs = null,
        string? refusal = null) =>
        new()
        {
            UserId = userId,
            Status = status,
            Counted = true,
            ExpiresAt = At(1).AddYears(50),
            PiecesShown = shown,
            PiecesPreTicked = preTicked,
            PiecesTicked = ticked,
            TickedPreTicked = tickedPreTicked,
            CategoryEdits = categoryEdits,
            ColourEdits = colourEdits,
            VisionMs = visionMs,
            Refusal = refusal,
        };

    private static Dictionary<string, string> BulkBlocks(string file)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var body = new List<string>();
        foreach (var line in file.ReplaceLineEndings("\n").Split('\n').Append("-- name: end"))
        {
            var bulk = BulkLine().Match(line);
            if (bulk.Success || line.StartsWith("-- name:", StringComparison.Ordinal) || line.StartsWith("-- league:", StringComparison.Ordinal) || line.StartsWith("-- tryon:", StringComparison.Ordinal))
            {
                if (name is not null)
                {
                    blocks[name] = Parameter().Replace(string.Join('\n', body), "@$1").Trim();
                }

                name = bulk.Success ? bulk.Groups[1].Value : null;
                body.Clear();
            }
            else if (name is not null)
            {
                body.Add(line);
            }
        }

        return blocks;
    }

    private static string MetricsFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "metrics.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("docs/metrics.sql was not found above the test binaries.");
    }

    [GeneratedRegex(@"^-- bulk: (\w+)\s*$")]
    private static partial Regex BulkLine();

    [GeneratedRegex(@"(?<![:\w]):(day_start|day_end)\b")]
    private static partial Regex Parameter();
}
