using System.Text.RegularExpressions;
using Npgsql;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The Kombin Ligi first-month metrics of <c>docs/metrics.sql</c> (the <c>-- league:</c> blocks, design note section 13) are run by hand, so
/// nothing else would notice a broken one: every block runs against the real schema with the psql parameters bound and returns one row.
/// </summary>
public sealed partial class LeagueMetricsSqlTests(ApiFactory factory)
{
    private static readonly string[] Expected =
    [
        "league_events", "league_comparisons", "league_join_rate", "league_usage_effect", "league_unmatched_pairs", "league_style_threshold", "league_engine_win_rate",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryLeagueMetricBlockRunsAndReturnsOneRow()
    {
        var blocks = LeagueBlocks(await File.ReadAllTextAsync(MetricsFile(), Ct));
        Assert.Equal(Expected, blocks.Keys);

        await using var connection = await factory.OpenConnectionAsync(Ct);
        foreach (var (name, sql) in blocks)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("day_start", new DateOnly(2020, 1, 1));
            command.Parameters.AddWithValue("day_end", new DateOnly(2041, 1, 1));
            await using var reader = await command.ExecuteReaderAsync(Ct);

            Assert.True(await reader.ReadAsync(Ct), name);
            Assert.False(await reader.ReadAsync(Ct), name);
        }
    }

    private static Dictionary<string, string> LeagueBlocks(string file)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var body = new List<string>();
        foreach (var line in file.ReplaceLineEndings("\n").Split('\n').Append("-- name: end"))
        {
            var league = LeagueLine().Match(line);
            // A job block or another hand-run section (try-on's "-- tryon:") ends a league block too.
            if (league.Success || line.StartsWith("-- name:", StringComparison.Ordinal) || line.StartsWith("-- tryon:", StringComparison.Ordinal))
            {
                if (name is not null)
                {
                    blocks[name] = Parameter().Replace(string.Join('\n', body), "@$1").Trim();
                }

                name = league.Success ? league.Groups[1].Value : null;
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

    [GeneratedRegex(@"^-- league: (\w+)\s*$")]
    private static partial Regex LeagueLine();

    [GeneratedRegex(@"(?<![:\w]):(day_start|day_end)\b")]
    private static partial Regex Parameter();
}
