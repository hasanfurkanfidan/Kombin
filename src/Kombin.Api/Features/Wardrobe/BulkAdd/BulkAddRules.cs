using Kombin.Api.Common.Time;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.BulkAdd;

/// <summary>
/// The decisions of bulk add that need no I/O (design: <c>docs/design/bulk-add.md</c>, sections "Limits" and "The job"): which batches are open,
/// the Istanbul day of the daily allowance, how long the job waits when vision is busy, when <c>process</c> starts an analysis, when a piece is
/// a likely duplicate. Every number comes from <see cref="BulkAddOptions"/>; none lives here.
/// </summary>
internal static class BulkAddRules
{
    /// <summary>The statuses a batch can no longer leave (and whose staging is gone or going): they do not count as open.</summary>
    public static readonly BulkBatchStatus[] EndedStatuses = [BulkBatchStatus.Confirmed, BulkBatchStatus.Cancelled, BulkBatchStatus.Expired];

    /// <summary>
    /// Whether the batch counts toward <c>BulkAdd:MaxOpenBatches</c>: not confirmed, cancelled or expired, and not past its staging time (the
    /// cleanup marks it <c>Expired</c> within the hour; the user must not wait for it).
    /// </summary>
    public static bool IsOpen(BulkBatchStatus status, DateTimeOffset expiresAt, DateTimeOffset now) =>
        !EndedStatuses.Contains(status) && expiresAt > now;

    /// <summary>The Istanbul calendar day containing <paramref name="now"/> as a half-open interval of UTC instants.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) DayWindow(DateTimeOffset now)
    {
        var day = Istanbul.Today(now);

        return (StartOfDay(day), StartOfDay(day.AddDays(1)));
    }

    /// <summary>How long until the next Istanbul midnight: the <c>retryAfterSeconds</c> of the daily limit's 429 (never below one second).</summary>
    public static TimeSpan UntilNextDay(DateTimeOffset now)
    {
        var untilMidnight = DayWindow(now).End - now;

        return untilMidnight < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : untilMidnight;
    }

    /// <summary>Whether a user who already used <paramref name="used"/> of the day's photos may start another.</summary>
    public static bool DailyAllowanceLeft(int used, int maxPerDay) => used < maxPerDay;

    /// <summary>
    /// How long the job waits before it asks vision once more: the service's <c>Retry-After</c> (or the minimum when it sent none, or one that does
    /// not parse) clamped to [<paramref name="min"/>, <paramref name="max"/>].
    /// </summary>
    public static TimeSpan RetryDelay(TimeSpan? retryAfter, TimeSpan min, TimeSpan max)
    {
        var wanted = retryAfter ?? min;

        return wanted < min ? min : wanted > max ? max : wanted;
    }

    /// <summary>
    /// Whether <c>process</c> has to start an analysis: a batch whose photo was never analysed (<c>Created</c>), a failed one (it starts again from
    /// scratch) and one that has been <c>Processing</c> for longer than <paramref name="reprocessAfter"/> (the job that held it probably died).
    /// <c>Ready</c> and <c>Refused</c> are answered as they are, and a recent <c>Processing</c> already has its job.
    /// </summary>
    public static bool NeedsAnalysis(BulkBatchStatus status, DateTimeOffset? processRequestedAt, DateTimeOffset now, TimeSpan reprocessAfter) => status switch
    {
        BulkBatchStatus.Created or BulkBatchStatus.Failed => true,
        BulkBatchStatus.Processing => processRequestedAt is null || processRequestedAt <= now - reprocessAfter,
        _ => false,
    };

    /// <summary>
    /// How many of the pieces vision found the batch keeps and how many it leaves out (counted, so the status answer can say so). Reading order
    /// is the service's: the first ones stay.
    /// </summary>
    public static (int Kept, int CutOff) KeepPieces(int found, int maxPieces)
    {
        var kept = Math.Min(Math.Max(found, 0), maxPieces);

        return (kept, Math.Max(found, 0) - kept);
    }

    /// <summary>Cosine distance (what pgvector's <c>&lt;=&gt;</c> returns) to similarity: 1 for the same direction.</summary>
    public static double Similarity(double cosineDistance) => 1 - cosineDistance;

    /// <summary>Whether a piece at <paramref name="cosineDistance"/> from one of the user's own items carries the "Dolabında benzeri var." hint.</summary>
    public static bool IsLikelyDuplicate(double cosineDistance, double threshold) => Similarity(cosineDistance) >= threshold;

    /// <summary>
    /// What the batch says when vision never produced an answer: <c>VisionBusy</c> when the last answer was <c>503 busy</c>, <c>PhotoUnreadable</c>
    /// for a permanent refusal of the file (a 4xx), otherwise the service was down, slow or failed on its side.
    /// </summary>
    public static BulkFailureCode FailureFor(int? status, string? detail, bool isTransient)
    {
        if (!isTransient)
        {
            return BulkFailureCode.PhotoUnreadable;
        }

        return status == 503 && string.Equals(detail, "busy", StringComparison.Ordinal) ? BulkFailureCode.VisionBusy : BulkFailureCode.VisionUnavailable;
    }

    private static DateTimeOffset StartOfDay(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Istanbul.Zone), TimeSpan.Zero);
}
