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

        var weeks = CohortWeeks.Previous(now, 2, QuietHours.Zone);

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

        Assert.Equal(new DateOnly(2026, 10, 19), CohortWeeks.Previous(sunday, 1, QuietHours.Zone)[0].WeekStart);
        Assert.Equal(new DateOnly(2026, 10, 26), CohortWeeks.Previous(monday, 1, QuietHours.Zone)[0].WeekStart);
    }

    [Fact]
    public void TheWeeksAreContiguousAndMostRecentFirst()
    {
        var weeks = CohortWeeks.Previous(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), 4, QuietHours.Zone);

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
}
