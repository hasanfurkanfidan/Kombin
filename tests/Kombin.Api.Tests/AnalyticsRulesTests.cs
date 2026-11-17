using Kombin.Api.Common.Time;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Notification;

namespace Kombin.Api.Tests;

public class AnalyticsRulesTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATimeWithinTheBoundsIsKept()
    {
        var occurred = Received.AddMinutes(-3);

        Assert.Equal(occurred, EventTiming.Clamp(occurred, Received));
    }

    [Fact]
    public void ATimeExactlyAtTheBoundsIsKept()
    {
        Assert.Equal(Received.AddDays(-7), EventTiming.Clamp(Received.AddDays(-7), Received));
        Assert.Equal(Received.AddMinutes(5), EventTiming.Clamp(Received.AddMinutes(5), Received));
    }

    [Fact]
    public void ATimeMoreThanSevenDaysOldTakesTheServerTime() =>
        Assert.Equal(Received, EventTiming.Clamp(Received.AddDays(-7).AddSeconds(-1), Received));

    [Fact]
    public void ATimeMoreThanFiveMinutesAheadTakesTheServerTime() =>
        Assert.Equal(Received, EventTiming.Clamp(Received.AddMinutes(5).AddSeconds(1), Received));

    [Fact]
    public void AKeptTimeIsNormalizedToUtc()
    {
        var local = new DateTimeOffset(2026, 11, 2, 12, 55, 0, TimeSpan.FromHours(3));

        var clamped = EventTiming.Clamp(local, Received);

        Assert.Equal(local, clamped);
        Assert.Equal(TimeSpan.Zero, clamped.Offset);
    }

    [Fact]
    public void LastWeekOnAMondayMorningIsTheWeekThatEndedAtMidnight()
    {
        // Monday 2026-11-02 08:00 in Istanbul (UTC+3).
        var now = new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

        var weeks = CohortWeeks.Previous(now, 2, Istanbul.Zone);

        Assert.Equal(new DateOnly(2026, 10, 26), weeks[0].WeekStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.Zero), weeks[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 21, 0, 0, TimeSpan.Zero), weeks[0].End);
        Assert.Equal(new DateOnly(2026, 10, 19), weeks[1].WeekStart);
        Assert.Equal(weeks[1].End, weeks[0].Start);
    }

    [Fact]
    public void TheWeekBoundaryIsMidnightInIstanbulNotInUtc()
    {
        // Sunday 23:30 in Istanbul is still the week of 2026-10-26; 00:30 on Monday starts the next one.
        var sunday = new DateTimeOffset(2026, 11, 1, 20, 30, 0, TimeSpan.Zero);
        var monday = new DateTimeOffset(2026, 11, 1, 21, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 10, 19), CohortWeeks.Previous(sunday, 1, Istanbul.Zone)[0].WeekStart);
        Assert.Equal(new DateOnly(2026, 10, 26), CohortWeeks.Previous(monday, 1, Istanbul.Zone)[0].WeekStart);
    }

    [Fact]
    public void TheWeeksAreContiguousAndMostRecentFirst()
    {
        var weeks = CohortWeeks.Previous(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), 4, Istanbul.Zone);

        Assert.Equal(4, weeks.Count);
        Assert.Equal(
            [new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 5)],
            weeks.Select(week => week.WeekStart));

        for (var index = 1; index < weeks.Count; index++)
        {
            Assert.Equal(weeks[index].End, weeks[index - 1].Start);
        }
    }

    [Fact]
    public void TheEmbeddedFileHoldsTheFourMetricsWithBoundParameters()
    {
        foreach (var metric in MetricNames.All)
        {
            var sql = MetricSql.Query(metric);

            Assert.Contains("@cohort_start", sql, StringComparison.Ordinal);
            Assert.Contains("@cohort_end", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(":cohort_start", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(":cohort_end", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("-- name:", sql, StringComparison.Ordinal);
        }

        Assert.Contains("@as_of::timestamptz", MetricSql.Query(MetricNames.D7Retention), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => MetricSql.Query("nope"));
    }

    [Fact]
    public void TheFileIsSplitAtItsNameLinesAndCastsAreLeftAlone()
    {
        const string file = """
            -- header, ignored: :cohort_start
            -- name: first
            SELECT :cohort_start::timestamptz, :as_of;
            -- name: second
            SELECT 'a:b'::text, :cohort_end
            """;

        var queries = MetricSql.Parse(file);

        Assert.Equal(["first", "second"], queries.Keys.Order());
        Assert.Equal("SELECT @cohort_start::timestamptz, @as_of;", queries["first"]);
        Assert.Equal("SELECT 'a:b'::text, @cohort_end", queries["second"]);
    }

    /// <summary>
    /// Task L5: the league's metric blocks (<c>-- league:</c>, run by hand) close the job's last block, so the job sees the same four metrics as
    /// before and none of them carries league SQL.
    /// </summary>
    [Fact]
    public void ALeagueLineClosesTheJobsLastBlockAndTheJobSeesTheSameFourMetrics()
    {
        var queries = MetricSql.Parse("""
            -- name: first
            SELECT 1;
            -- league: league_x
            SELECT 2 FROM league.pairs;
            -- name: second
            SELECT 3
            """);

        Assert.Equal(["first", "second"], queries.Keys.Order());
        Assert.Equal("SELECT 1;", queries["first"]);

        Assert.All(MetricNames.All, metric => Assert.DoesNotContain("league.", MetricSql.Query(metric), StringComparison.Ordinal));
        Assert.All(MetricNames.All, metric => Assert.DoesNotContain("-- league:", MetricSql.Query(metric), StringComparison.Ordinal));
        Assert.Equal(4, MetricNames.All.Count);
    }
}

public class EventCapTests
{
    [Theory]
    [InlineData("2026-11-15T22:30:00Z", "2026-11-15T21:00:00Z")]
    [InlineData("2026-11-15T20:59:00Z", "2026-11-14T21:00:00Z")]
    [InlineData("2026-11-15T21:00:00Z", "2026-11-15T21:00:00Z")]
    public void TheDayStartsAtMidnightInIstanbul(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), EventCap.DayStart(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), Istanbul.Zone));

    [Theory]
    [InlineData(0, 5000, 5000)]
    [InlineData(4998, 5000, 2)]
    [InlineData(6000, 5000, 0)]
    public void WhatIsLeftOfTheCap(int storedToday, int cap, int remaining) => Assert.Equal(remaining, EventCap.Remaining(storedToday, cap));
}
