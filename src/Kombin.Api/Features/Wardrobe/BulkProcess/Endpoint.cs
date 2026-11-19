using System.Globalization;
using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Wardrobe.BulkProcess;

internal sealed record BulkProcessResponse(Guid BatchId, BulkBatchStatus Status);

internal static class BulkProcessEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/process", Handle)
            .WithName("ProcessWardrobeBulkBatch")
            .WithSummary("Checks the uploaded photo and starts the analysis; the app then polls GET /bulk/{id}.")
            .RequireRateLimiting(RateLimitPolicies.Uploads)
            .Produces<BulkProcessResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Enqueues <c>bulk-add-analyze</c>. Idempotent like the single add's <c>process</c>: <c>Ready</c> and <c>Refused</c> are returned as they are, a
    /// <c>Processing</c> batch requested within <c>BulkAdd:ReprocessAfter</c> gets nothing new, a <c>Failed</c> one starts again from scratch. A
    /// confirmed, cancelled or expired batch is a 409. The daily allowance is checked only when an analysis would start; at the limit the batch stays
    /// <c>Created</c> and nothing was spent.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id,
        HttpContext http,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IBackgroundJobClient jobs,
        IClock clock,
        IOptions<BulkAddOptions> options,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var batch = await db.BulkBatches.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (batch is null)
        {
            return BulkAddErrors.NotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        switch (batch.Status)
        {
            case BulkBatchStatus.Confirmed:
                return BulkAddErrors.AlreadyConfirmed.ToHttpResult();
            case BulkBatchStatus.Cancelled or BulkBatchStatus.Expired:
                return BulkAddErrors.Expired.ToHttpResult();
        }

        if (batch.ExpiresAt <= now)
        {
            return BulkAddErrors.Expired.ToHttpResult();
        }

        var accepted = TypedResults.Accepted($"/api/v1/me/wardrobe/bulk/{batch.Id}", new BulkProcessResponse(batch.Id, batch.Status));
        if (!BulkAddRules.NeedsAnalysis(batch.Status, batch.ProcessRequestedAt, now, options.Value.ReprocessAfter))
        {
            return accepted;
        }

        if (batch.OriginalKey is null)
        {
            return WardrobeErrors.NothingToProcess.ToHttpResult();
        }

        // Fail fast on a missing or oversized upload instead of letting the job find out.
        var size = await storage.GetSizeAsync(batch.OriginalKey, cancellationToken);
        if (size is null)
        {
            return WardrobeErrors.UploadMissing.ToHttpResult();
        }

        if (size > WardrobeLimits.MaxImageBytes)
        {
            // The presigned PUT cannot cap the size, so an oversized object goes at once rather than at the cleanup.
            await storage.DeleteAsync([batch.OriginalKey], cancellationToken);
            return WardrobeErrors.ImageTooLarge.ToHttpResult();
        }

        // Photos of the Istanbul day that count: answered by vision (refusals included), or still being analysed (they will count unless they
        // fail), so a burst of simultaneous calls cannot all slip under the limit. This batch's own earlier attempt never counts against it.
        var (dayStart, dayEnd) = BulkAddRules.DayWindow(now);
        var used = await db.BulkBatches.CountAsync(
            candidate => candidate.UserId == userId
                && candidate.Id != batch.Id
                && candidate.ProcessRequestedAt >= dayStart
                && candidate.ProcessRequestedAt < dayEnd
                && (candidate.Counted || candidate.Status == BulkBatchStatus.Processing),
            cancellationToken);
        if (!BulkAddRules.DailyAllowanceLeft(used, options.Value.MaxPhotosPerDay))
        {
            return DailyLimitResult(http, BulkAddRules.UntilNextDay(now));
        }

        batch.Status = BulkBatchStatus.Processing;
        batch.FailureCode = null;
        batch.FailureDetail = null;
        batch.Refusal = null;
        batch.Counted = false;
        batch.AnalysisAttempts = 0;
        batch.ProcessRequestedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        jobs.Enqueue<BulkAddAnalyzeJob>(job => job.RunAsync(batch.Id, CancellationToken.None));

        return TypedResults.Accepted($"/api/v1/me/wardrobe/bulk/{batch.Id}", new BulkProcessResponse(batch.Id, batch.Status));
    }

    /// <summary>The daily limit in the rate limiter's shape: 429, <c>errorCode</c>, <c>retryAfterSeconds</c> and a <c>Retry-After</c> header (the next Istanbul midnight).</summary>
    private static IResult DailyLimitResult(HttpContext http, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return Problems.Create(
            http,
            StatusCodes.Status429TooManyRequests,
            title: "Too many photos today.",
            detail: "The daily limit of bulk add photos is reached. Try again after the time in the Retry-After header.",
            errorCode: BulkAddErrors.DailyLimitCode,
            extra: new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds });
    }
}
