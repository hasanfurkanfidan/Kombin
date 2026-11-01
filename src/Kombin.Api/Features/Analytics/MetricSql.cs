using System.Data;
using System.Text.RegularExpressions;
using Kombin.Api.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Kombin.Api.Features.Analytics;

internal static class MetricNames
{
    public const string D7Retention = "d7_retention";

    public const string AlertRate = "alert_rate";

    public const string ClickRate = "click_rate";

    public const string OnboardingCompletion = "onboarding_completion";

    public static readonly IReadOnlyList<string> All = [D7Retention, AlertRate, ClickRate, OnboardingCompletion];
}

internal readonly record struct MetricCounts(long Numerator, long Denominator);

/// <summary>
/// Runs the four metric queries of <c>docs/metrics.sql</c>, which is embedded in the assembly: the file stays the one place with the SQL
/// (it is also what an operator runs by hand), and its <c>:name</c> parameters are bound as <c>@name</c> here.
/// </summary>
internal static partial class MetricSql
{
    private const string ResourceName = "Kombin.Api.Analytics.metrics.sql";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Queries = new(Load);

    public static string Query(string metric) =>
        Queries.Value.TryGetValue(metric, out var sql) ? sql : throw new ArgumentException($"There is no query for the metric '{metric}'.", nameof(metric));

    /// <summary>Cohort = actors whose first app_open is in [<paramref name="cohortStart"/>, <paramref name="cohortEnd"/>); <paramref name="asOf"/> is "now" for the D7 maturity rule.</summary>
    public static async Task<MetricCounts> RunAsync(
        KombinDbContext db, string metric, DateTimeOffset cohortStart, DateTimeOffset cohortEnd, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;

        if (!wasOpen)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = (NpgsqlCommand)connection.CreateCommand();
            command.CommandText = Query(metric);
            command.Parameters.Add(new NpgsqlParameter("cohort_start", NpgsqlDbType.TimestampTz) { Value = cohortStart.ToUniversalTime() });
            command.Parameters.Add(new NpgsqlParameter("cohort_end", NpgsqlDbType.TimestampTz) { Value = cohortEnd.ToUniversalTime() });
            command.Parameters.Add(new NpgsqlParameter("as_of", NpgsqlDbType.TimestampTz) { Value = asOf.ToUniversalTime() });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            return new MetricCounts(reader.GetInt64(0), reader.GetInt64(1));
        }
        finally
        {
            if (!wasOpen)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>Splits the file at its <c>-- name: x</c> lines and turns the psql-style <c>:param</c> into Npgsql's <c>@param</c> (a <c>::cast</c> is left alone).</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string file)
    {
        var queries = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var body = new List<string>();

        foreach (var line in file.ReplaceLineEndings("\n").Split('\n'))
        {
            var header = NameLine().Match(line);

            if (header.Success)
            {
                Flush();
                name = header.Groups[1].Value;
            }
            else if (name is not null)
            {
                body.Add(line);
            }
        }

        Flush();

        return queries;

        void Flush()
        {
            if (name is not null)
            {
                queries[name] = Parameter().Replace(string.Join('\n', body), "@$1").Trim();
            }

            body.Clear();
        }
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(MetricSql).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' (docs/metrics.sql) is missing.");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    [GeneratedRegex(@"^-- name: (\w+)\s*$")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"(?<![:\w]):(cohort_start|cohort_end|as_of)\b")]
    private static partial Regex Parameter();
}
