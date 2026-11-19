using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Tests;

/// <summary>The pure decisions of bulk add (task BA3, PR 2): open batches, the Istanbul day, the retry delay, when <c>process</c> starts an analysis, the hint.</summary>
public sealed class BulkAddRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Created", true)]
    [InlineData("Processing", true)]
    [InlineData("Ready", true)]
    [InlineData("Refused", true)]
    [InlineData("Failed", true)]
    [InlineData("Confirmed", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Expired", false)]
    public void ABatchIsOpenUntilItEnds(string status, bool open) =>
        Assert.Equal(open, BulkAddRules.IsOpen(Enum.Parse<BulkBatchStatus>(status), Noon.AddHours(1), Noon));

    [Fact]
    public void ABatchPastItsStagingTimeIsNotOpenEvenBeforeTheCleanupMarksIt()
    {
        Assert.False(BulkAddRules.IsOpen(BulkBatchStatus.Ready, Noon, Noon));
        Assert.False(BulkAddRules.IsOpen(BulkBatchStatus.Ready, Noon.AddSeconds(-1), Noon));
        Assert.True(BulkAddRules.IsOpen(BulkBatchStatus.Ready, Noon.AddSeconds(1), Noon));
    }

    [Theory]
    [InlineData("2026-11-18T20:59:59Z", "2026-11-17T21:00:00Z", "2026-11-18T21:00:00Z")] // 23:59:59 Istanbul: still the 18th
    [InlineData("2026-11-18T21:00:00Z", "2026-11-18T21:00:00Z", "2026-11-19T21:00:00Z")] // 00:00:00 on the 19th
    [InlineData("2026-11-18T20:30:00Z", "2026-11-17T21:00:00Z", "2026-11-18T21:00:00Z")] // 23:30 Istanbul
    [InlineData("2026-11-18T21:30:00Z", "2026-11-18T21:00:00Z", "2026-11-19T21:00:00Z")] // 00:30 Istanbul
    public void TheDayIsTheIstanbulDay(string now, string start, string end)
    {
        var (from, to) = BulkAddRules.DayWindow(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture), from);
        Assert.Equal(DateTimeOffset.Parse(end, System.Globalization.CultureInfo.InvariantCulture), to);
    }

    [Fact]
    public void TheRetryAfterOfTheLimitIsTheTimeToTheNextIstanbulMidnight()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), BulkAddRules.UntilNextDay(DateTimeOffset.Parse("2026-11-18T20:30:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(TimeSpan.FromHours(23.5), BulkAddRules.UntilNextDay(DateTimeOffset.Parse("2026-11-18T21:30:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(TimeSpan.FromSeconds(1), BulkAddRules.UntilNextDay(DateTimeOffset.Parse("2026-11-18T20:59:59.700Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(0, 8, true)]
    [InlineData(7, 8, true)]
    [InlineData(8, 8, false)]
    [InlineData(9, 8, false)]
    public void TheDayAllowsExactlyTheSettingsNumberOfPhotos(int used, int max, bool allowed) =>
        Assert.Equal(allowed, BulkAddRules.DailyAllowanceLeft(used, max));

    [Theory]
    [InlineData(null, 3)]    // no Retry-After: the minimum
    [InlineData(1, 3)]       // below: clamped up
    [InlineData(3, 3)]
    [InlineData(5, 5)]       // inside: as sent
    [InlineData(10, 10)]
    [InlineData(25, 10)]     // above: clamped down
    public void BusyWaitsTheRetryAfterClampedToThreeToTenSeconds(int? retryAfter, int expectedSeconds)
    {
        var delay = BulkAddRules.RetryDelay(retryAfter is null ? null : TimeSpan.FromSeconds(retryAfter.Value), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void ZeroSettingsMeanNoWaiting() =>
        Assert.Equal(TimeSpan.Zero, BulkAddRules.RetryDelay(TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.Zero));

    [Theory]
    [InlineData("Created", null, true)]
    [InlineData("Failed", 1, true)]                  // a failed batch starts again from scratch
    [InlineData("Processing", null, true)]
    [InlineData("Processing", -9, false)]            // requested 9 minutes ago: its job is still expected
    [InlineData("Processing", -10, true)]            // 10 minutes: the job probably died
    [InlineData("Ready", -60, false)]
    [InlineData("Refused", -60, false)]
    [InlineData("Confirmed", -60, false)]
    public void ProcessStartsAnAnalysisOnlyWhereTheNoteSaysSo(string status, int? requestedMinutesFromNow, bool starts)
    {
        DateTimeOffset? requested = requestedMinutesFromNow is { } minutes ? Noon.AddMinutes(minutes) : null;

        Assert.Equal(starts, BulkAddRules.NeedsAnalysis(Enum.Parse<BulkBatchStatus>(status), requested, Noon, TimeSpan.FromMinutes(10)));
    }

    [Theory]
    [InlineData(3, 12, 3, 0)]
    [InlineData(12, 12, 12, 0)]
    [InlineData(15, 12, 12, 3)]
    [InlineData(5, 4, 4, 1)]
    [InlineData(0, 12, 0, 0)]
    public void PiecesAboveTheLimitAreCutOffAndCounted(int found, int max, int kept, int cutOff) =>
        Assert.Equal((kept, cutOff), BulkAddRules.KeepPieces(found, max));

    [Theory]
    [InlineData(0.25, 0.75, true)]   // similarity 0.75 exactly: at the threshold counts
    [InlineData(0.2, 0.75, true)]
    [InlineData(0.26, 0.75, false)]
    [InlineData(0.0, 0.92, true)]
    [InlineData(1.0, 0.92, false)]
    public void AHintNeedsTheThresholdOrMore(double distance, double threshold, bool duplicate) =>
        Assert.Equal(duplicate, BulkAddRules.IsLikelyDuplicate(distance, threshold));

    [Theory]
    [InlineData(503, "busy", true, "VisionBusy")]
    [InlineData(503, "model_loading", true, "VisionUnavailable")]
    [InlineData(null, null, true, "VisionUnavailable")]
    [InlineData(500, "boom", true, "VisionUnavailable")]
    [InlineData(400, "unsupported_image", false, "PhotoUnreadable")]
    [InlineData(null, null, false, "PhotoUnreadable")]
    public void TheFailureCodeFollowsTheLastAnswer(int? status, string? detail, bool transient, string expected) =>
        Assert.Equal(Enum.Parse<BulkFailureCode>(expected), BulkAddRules.FailureFor(status, detail, transient));
}
