using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Persistence.Entities.TryOn;

namespace Kombin.Api.Features.TryOn.Rules;

/// <summary>How urgent a prepare is; the job works in this order (<see cref="None"/>: not due).</summary>
internal enum PrepareUrgency
{
    None,

    /// <summary>A request found the piece without an answer and asked for it.</summary>
    Requested,

    /// <summary>Never prepared.</summary>
    New,

    /// <summary>Its photos (or category) changed since the last attempt.</summary>
    SourceChanged,

    /// <summary>A transient failure's one retry, or the daily retry of a failed or refused re-prepare.</summary>
    Retry,

    /// <summary>Answered by another version of the service: low priority, in batches.</summary>
    Version,
}

/// <summary>What the job knows of a garment row when it decides whether to prepare it.</summary>
internal sealed record GarmentRowState(
    TryOnGarmentStatus Status,
    string SourceHash,
    string? PreparedHash,
    string? PrepareVersion,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset? RequestedAt)
{
    public static GarmentRowState Of(TryOnGarment row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new(row.Status, row.SourceHash, row.PreparedHash, row.PrepareVersion, row.Attempts, row.NextAttemptAt, row.LeaseUntil, row.RequestedAt);
    }
}

/// <summary>A failure's effect on the row.</summary>
internal sealed record FailureOutcome(TryOnGarmentStatus Status, int Attempts, DateTimeOffset? NextAttemptAt);

/// <summary>
/// When a garment is prepared and what a failure does (<c>docs/design/try-on.md</c>, "The job"). Pure.
/// <list type="bullet">
/// <item>Due: no row; the last attempt was about another source; a <c>Pending</c> or <c>Failed</c> row whose next attempt time passed; an answered
/// row whose current source has no answer yet or whose answer is of another version, once its back-off passed. Never while another prepare
/// holds the row's lease, never a <c>Refused</c> row of the same source.</item>
/// <item>A failure: a 4xx is permanent (<c>Refused</c>, or, for a row that has an answer, the answer stays and the next try waits
/// <c>FailedRetryAfter</c>); a transient one (503 <c>busy</c>, 5xx, network) is tried once more after <c>RetryAfter</c>, the second makes the row
/// <c>Failed</c> (or keeps the answer) with the next try after <c>FailedRetryAfter</c>.</item>
/// </list>
/// </summary>
internal static class TryOnPrepareRules
{
    /// <summary>The first try plus the one later retry of the brief.</summary>
    public const int TransientTries = 2;

    public static PrepareUrgency Due(GarmentRowState? row, string sourceHash, string? currentVersion, DateTimeOffset now)
    {
        if (row is null)
        {
            return PrepareUrgency.New;
        }

        if (row.LeaseUntil > now)
        {
            return PrepareUrgency.None;
        }

        var waiting = row.NextAttemptAt > now;
        var requested = row.RequestedAt is not null && row.Attempts == 0 && row.Status == TryOnGarmentStatus.Pending;

        if (row.SourceHash != sourceHash)
        {
            // A new source deserves a try now, except while an answered row waits out the back-off of a failed re-prepare.
            return row.Status == TryOnGarmentStatus.Prepared && waiting ? PrepareUrgency.None
                : requested ? PrepareUrgency.Requested
                : PrepareUrgency.SourceChanged;
        }

        return row.Status switch
        {
            TryOnGarmentStatus.Refused => PrepareUrgency.None,
            _ when waiting => PrepareUrgency.None,
            TryOnGarmentStatus.Pending => requested ? PrepareUrgency.Requested : PrepareUrgency.Retry,
            TryOnGarmentStatus.Failed => PrepareUrgency.Retry,
            _ when row.PreparedHash != sourceHash => PrepareUrgency.Retry,
            _ when currentVersion is not null && row.PrepareVersion != currentVersion => PrepareUrgency.Version,
            _ => PrepareUrgency.None,
        };
    }

    /// <summary>What a failed prepare leaves on the row; <paramref name="attempts"/> are the transient failures before this one (of this source).</summary>
    public static FailureOutcome AfterFailure(bool hasAnswer, int attempts, bool transient, DateTimeOffset now, TimeSpan retryAfter, TimeSpan failedRetryAfter)
    {
        if (!transient)
        {
            return hasAnswer
                ? new FailureOutcome(TryOnGarmentStatus.Prepared, 0, now + failedRetryAfter)
                : new FailureOutcome(TryOnGarmentStatus.Refused, 0, null);
        }

        var tries = attempts + 1;
        if (tries < TransientTries)
        {
            return new FailureOutcome(hasAnswer ? TryOnGarmentStatus.Prepared : TryOnGarmentStatus.Pending, tries, now + retryAfter);
        }

        return new FailureOutcome(hasAnswer ? TryOnGarmentStatus.Prepared : TryOnGarmentStatus.Failed, 0, now + failedRetryAfter);
    }

    /// <summary>Whether the catalogue may prepare more than the day's trickle: Istanbul time inside [start, end), the window may wrap midnight.</summary>
    public static bool IsNight(TimeOnly istanbulTime, TimeOnly start, TimeOnly end) =>
        start <= end ? istanbulTime >= start && istanbulTime < end : istanbulTime >= start || istanbulTime < end;
}

/// <summary>
/// The source hash of a garment: what is sent to the service. The job's SQL computes the very same string
/// (<c>encode(sha256(convert_to(…, 'UTF8')), 'hex')</c>): change both together.
/// </summary>
internal static class TryOnSources
{
    public static string Wardrobe(string categorySlug, string cutoutKey) => Hash($"wardrobe|{categorySlug}|{cutoutKey}");

    /// <summary>The first six URLs in gallery order (<c>sort_order</c>, then ID, as PostgreSQL orders them).</summary>
    public static string Product(string categorySlug, IEnumerable<string> urls) => Hash($"product|{categorySlug}|{string.Join('\n', urls.Take(Common.Vision.VisionLimits.MaxPrepareImages))}");

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
