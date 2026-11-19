using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Vision;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Kombin.Api.Features.Wardrobe.Jobs;

internal enum BulkAnalyzeOutcome
{
    /// <summary>Nothing was done: the flag is off, the batch is gone, not <c>Processing</c>, already claimed by a run, or ended while the job worked.</summary>
    Skipped,

    /// <summary>The pieces are staged and the photo is deleted.</summary>
    Ready,

    /// <summary>Vision refused the photo; the photo is deleted.</summary>
    Refused,

    /// <summary>The batch is <c>Failed</c> (not counted); the photo is kept so "tekrar dene" needs no second upload.</summary>
    Failed,
}

/// <summary>
/// "Toplu ekle": the photo of several garments → <c>kombin-vision</c>'s <c>/v1/garment/bulk</c> → the staged pieces of a review screen (design:
/// <c>docs/design/bulk-add.md</c>, "The job"). Fire-and-forget, enqueued by <c>process</c>, <c>[AutomaticRetry(Attempts = 0)]</c>: the one retry
/// is ours (a busy or failing service is asked once more after its <c>Retry-After</c>, clamped). It claims the batch with a conditional update
/// (<c>Processing</c> with no attempt yet), so a second run exits. A piece is the wardrobe analysis of the single add, mapped by the same code
/// (<see cref="WardrobeAnalysis"/>, the same 0.35 category threshold, defaults, warnings and 512-float check). The cut-out and the thumbnail are
/// written to private staged keys (<c>bulk/{user}/{batch}/{piece}/staged.png</c> and <c>stagedthumb.webp</c>), never to <c>cutout.*</c> or
/// <c>thumb.*</c>. <c>Ready</c> and <c>Refused</c> delete the room photo at once, <c>Failed</c> keeps it. Every write after the vision call
/// only applies to a batch that is still <c>Processing</c>, so a cancel that arrives meanwhile wins.
/// </summary>
internal sealed partial class BulkAddAnalyzeJob(
    KombinDbContext db, IObjectStorage storage, IVisionClient vision, IClock clock, IOptions<BulkAddOptions> options, ILogger<BulkAddAnalyzeJob> logger)
{
    public const string JobId = "bulk-add-analyze";

    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("bulk-add-analyze {0}")]
    public async Task RunAsync(Guid batchId, CancellationToken cancellationToken) => await AnalyzeAsync(batchId, cancellationToken);

    public async Task<BulkAnalyzeOutcome> AnalyzeAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return BulkAnalyzeOutcome.Skipped;
        }

        // The claim: only one run moves the batch from "no attempt yet" to "attempt 1".
        var claimed = await db.BulkBatches
            .Where(candidate => candidate.Id == batchId && candidate.Status == BulkBatchStatus.Processing && candidate.AnalysisAttempts == 0)
            .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.AnalysisAttempts, 1), cancellationToken);
        if (claimed == 0)
        {
            return BulkAnalyzeOutcome.Skipped;
        }

        var batch = await db.BulkBatches.AsNoTracking().SingleAsync(candidate => candidate.Id == batchId, cancellationToken);

        var original = batch.OriginalKey is null ? null : await storage.GetAsync(batch.OriginalKey, cancellationToken);
        if (original is null)
        {
            return await FailAsync(batch, BulkFailureCode.PhotoUnreadable, "original_missing", cancellationToken);
        }

        if (original.Content.LongLength > WardrobeLimits.MaxImageBytes)
        {
            return await FailAsync(batch, BulkFailureCode.PhotoUnreadable, "image_too_large", cancellationToken);
        }

        var call = await vision.BulkAsync(original.Content, original.ContentType, settings.VisionTimeout, cancellationToken);
        if (call.Value is null && call.IsTransient)
        {
            // One more try, after the service's own Retry-After (clamped): a busy service usually has a free place a few seconds later.
            var delay = BulkAddRules.RetryDelay(call.RetryAfter, settings.BusyRetryMin, settings.BusyRetryMax);
            await db.BulkBatches
                .Where(candidate => candidate.Id == batchId && candidate.Status == BulkBatchStatus.Processing)
                .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.AnalysisAttempts, 2), cancellationToken);
            LogRetry(logger, batchId, call.Error ?? "vision_failed", (int)delay.TotalMilliseconds);

            await Task.Delay(delay, cancellationToken);
            call = await vision.BulkAsync(original.Content, original.ContentType, settings.VisionTimeout, cancellationToken);
        }

        if (call.Value is not { } answer)
        {
            return await FailAsync(batch, BulkAddRules.FailureFor(call.Status, call.Detail, call.IsTransient), call.Error ?? "vision_failed", cancellationToken);
        }

        var visionMs = answer.StagesMs is { } stages && stages.TryGetValue("total", out var total) ? (int?)Math.Round(total) : null;

        if (!string.IsNullOrWhiteSpace(answer.Refusal))
        {
            return await RefuseAsync(batch, answer.Refusal.Trim(), visionMs, cancellationToken);
        }

        var (kept, cutOff) = BulkAddRules.KeepPieces(answer.Pieces.Count, settings.MaxPiecesPerPhoto);
        var found = answer.Pieces.Take(kept).ToList();

        // Decode and check everything before anything is written: one bad piece fails the batch, as a bad answer fails the single add.
        var decoded = new List<(byte[] Cutout, byte[] Thumbnail)>(found.Count);
        foreach (var piece in found)
        {
            try
            {
                decoded.Add((Convert.FromBase64String(piece.Analysis.Cutout), Convert.FromBase64String(piece.Analysis.Thumbnail)));
            }
            catch (FormatException)
            {
                return await FailAsync(batch, BulkFailureCode.AnalysisFailed, "vision_bad_response: images are not base64", cancellationToken);
            }

            if (piece.Analysis.Embedding.Length != WardrobeItem.EmbeddingDimensions)
            {
                return await FailAsync(
                    batch, BulkFailureCode.AnalysisFailed, $"vision_bad_response: embedding has {piece.Analysis.Embedding.Length} dimensions", cancellationToken);
            }
        }

        var model = await vision.GetEmbeddingModelAsync(cancellationToken);
        var slugs = found.Select(piece => WardrobeAnalysis.Map(piece.Analysis).CategorySlug).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var categoryIds = await db.Categories.AsNoTracking().Where(category => slugs.Contains(category.Slug)).ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);

        var pieces = new List<BulkPiece>(found.Count);
        var stagedKeys = new List<string>();
        try
        {
            for (var index = 0; index < found.Count; index++)
            {
                var source = found[index];
                var attributes = WardrobeAnalysis.Map(source.Analysis);
                Guid? categoryId = attributes.CategorySlug is { } slug && categoryIds.TryGetValue(slug, out var id) ? id : null;
                var vector = new Vector(source.Analysis.Embedding);

                var piece = new BulkPiece
                {
                    BatchId = batch.Id,
                    UserId = batch.UserId,
                    SortOrder = index,
                    BoxX = source.Box.X,
                    BoxY = source.Box.Y,
                    BoxW = source.Box.W,
                    BoxH = source.Box.H,
                    PreTicked = source.PreTicked,
                    Reasons = [.. source.Reasons.Distinct(StringComparer.Ordinal)],
                    CategoryId = categoryId,
                    PrimaryColor = attributes.PrimaryColor,
                    SecondaryColor = attributes.SecondaryColor,
                    Pattern = attributes.Pattern,
                    Warnings = attributes.Warnings,
                    Embedding = vector,
                    EmbeddingModel = model,
                    AnalysisJson = WardrobeAnalysis.ToJson(source.Analysis, model, clock.UtcNow),
                    SimilarItemId = model is null ? null : await FindDuplicateAsync(batch.UserId, vector, model, settings.DuplicateSimilarity, cancellationToken),
                };

                // As the single add: formality and seasons keep the entity's defaults when no category was accepted.
                if (categoryId is not null)
                {
                    piece.Formality = attributes.Formality;
                    piece.Seasons = attributes.Seasons;
                }

                // Private names, same keys on every run: a rerun overwrites instead of leaving orphans.
                piece.CutoutKey = StorageKeys.BuildBulk(batch.UserId, batch.Id, piece.Id, ImageVariant.Staged, "png");
                piece.ThumbKey = StorageKeys.BuildBulk(batch.UserId, batch.Id, piece.Id, ImageVariant.StagedThumb, "webp");
                stagedKeys.Add(piece.CutoutKey);
                stagedKeys.Add(piece.ThumbKey);
                await storage.PutAsync(piece.CutoutKey, decoded[index].Cutout, "image/png", cancellationToken);
                await storage.PutAsync(piece.ThumbKey, decoded[index].Thumbnail, "image/webp", cancellationToken);
                pieces.Add(piece);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStagingFailed(logger, exception, batchId);
            await TryDeleteAsync(stagedKeys);

            return await FailAsync(batch, BulkFailureCode.AnalysisFailed, "staging_failed", cancellationToken);
        }

        var committed = await CommitReadyAsync(batch, pieces, kept: found.Count, cutOff, model, visionMs, answer.Unseparated, cancellationToken);
        if (!committed)
        {
            // Cancelled or expired while vision worked: nothing of this run may stay.
            await TryDeleteAsync(stagedKeys);

            return BulkAnalyzeOutcome.Skipped;
        }

        await DeletePhotoAsync(batch, cancellationToken);
        LogReady(logger, batchId, pieces.Count, cutOff);

        return BulkAnalyzeOutcome.Ready;
    }

    /// <summary>
    /// The nearest of the user's own ready, not archived items of the same vision model; at or above <c>BulkAdd:DuplicateSimilarity</c> its ID is
    /// the piece's hint. Only the caller's items are ever asked (<c>user_id</c> in the query), so another user's wardrobe can never be a hint.
    /// </summary>
    private async Task<Guid?> FindDuplicateAsync(Guid userId, Vector vector, string model, double threshold, CancellationToken cancellationToken)
    {
        var nearest = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && !item.IsArchived && item.Status == WardrobeItemStatus.Ready && item.Embedding != null && item.EmbeddingModel == model)
            .OrderBy(item => item.Embedding!.CosineDistance(vector))
            .Select(item => new { item.Id, Distance = item.Embedding!.CosineDistance(vector) })
            .FirstOrDefaultAsync(cancellationToken);

        return nearest is not null && BulkAddRules.IsLikelyDuplicate(nearest.Distance, threshold) ? nearest.Id : null;
    }

    /// <summary>
    /// Pieces and <c>Ready</c> in one transaction, under the batch's row lock (the one confirm and cancel will take): false when the batch is no
    /// longer <c>Processing</c>, in which case nothing is written.
    /// </summary>
    private async Task<bool> CommitReadyAsync(
        BulkBatch batch, List<BulkPiece> pieces, int kept, int cutOff, string? model, int? visionMs, VisionUnseparated? unseparated, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var id = batch.Id;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var status = await db.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM wardrobe.bulk_batches WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (!string.Equals(status, nameof(BulkBatchStatus.Processing), StringComparison.Ordinal))
        {
            return false;
        }

        db.BulkPieces.AddRange(pieces);
        await db.SaveChangesAsync(cancellationToken);

        var preTicked = pieces.Count(piece => piece.PreTicked);
        var unseparatedCount = unseparated?.Count ?? 0;
        var regions = unseparated is { Regions.Count: > 0 } ? BulkBatchResponses.RegionsToJson(unseparated.Regions) : null;
        await db.BulkBatches.Where(candidate => candidate.Id == id).ExecuteUpdateAsync(
            set => set
                .SetProperty(candidate => candidate.Status, BulkBatchStatus.Ready)
                .SetProperty(candidate => candidate.Counted, true)
                .SetProperty(candidate => candidate.CutOffCount, cutOff)
                .SetProperty(candidate => candidate.UnseparatedCount, unseparatedCount)
                .SetProperty(candidate => candidate.UnseparatedRegions, regions)
                .SetProperty(candidate => candidate.VisionMs, visionMs)
                .SetProperty(candidate => candidate.EmbeddingModel, model)
                .SetProperty(candidate => candidate.PiecesShown, kept)
                .SetProperty(candidate => candidate.PiecesPreTicked, preTicked)
                .SetProperty(candidate => candidate.FailureCode, (BulkFailureCode?)null)
                .SetProperty(candidate => candidate.FailureDetail, (string?)null)
                .SetProperty(candidate => candidate.UpdatedAt, now),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return true;
    }

    private async Task<BulkAnalyzeOutcome> RefuseAsync(BulkBatch batch, string refusal, int? visionMs, CancellationToken cancellationToken)
    {
        var id = batch.Id;
        var now = clock.UtcNow;
        var code = refusal.Length <= 40 ? refusal : refusal[..40];

        // A refusal is the user's photo: it counts toward the day's allowance.
        var changed = await db.BulkBatches
            .Where(candidate => candidate.Id == id && candidate.Status == BulkBatchStatus.Processing)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.Status, BulkBatchStatus.Refused)
                    .SetProperty(candidate => candidate.Refusal, code)
                    .SetProperty(candidate => candidate.Counted, true)
                    .SetProperty(candidate => candidate.VisionMs, visionMs)
                    .SetProperty(candidate => candidate.UpdatedAt, now),
                cancellationToken);
        if (changed == 0)
        {
            return BulkAnalyzeOutcome.Skipped;
        }

        await DeletePhotoAsync(batch, cancellationToken);
        LogRefused(logger, id, code);

        return BulkAnalyzeOutcome.Refused;
    }

    private async Task<BulkAnalyzeOutcome> FailAsync(BulkBatch batch, BulkFailureCode code, string detail, CancellationToken cancellationToken)
    {
        var id = batch.Id;
        var now = clock.UtcNow;
        var clipped = detail.Length <= 300 ? detail : detail[..300];

        // Not counted, and the photo stays: "tekrar dene" (process again) needs no second upload.
        await db.BulkBatches
            .Where(candidate => candidate.Id == id && candidate.Status == BulkBatchStatus.Processing)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.Status, BulkBatchStatus.Failed)
                    .SetProperty(candidate => candidate.FailureCode, code)
                    .SetProperty(candidate => candidate.FailureDetail, clipped)
                    .SetProperty(candidate => candidate.Counted, false)
                    .SetProperty(candidate => candidate.UpdatedAt, now),
                cancellationToken);
        LogFailed(logger, id, code.ToString(), detail);

        return BulkAnalyzeOutcome.Failed;
    }

    /// <summary>
    /// Deletes the room photo and forgets its key. A storage failure is logged and leaves the key: the photo then goes with the batch (confirm,
    /// cancel, the cleanup and the account purger all delete by the batch's prefix).
    /// </summary>
    private async Task DeletePhotoAsync(BulkBatch batch, CancellationToken cancellationToken)
    {
        if (batch.OriginalKey is not { } key)
        {
            return;
        }

        try
        {
            await storage.DeleteAsync([key], cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPhotoDeleteFailed(logger, exception, batch.Id);
            return;
        }

        var id = batch.Id;
        await db.BulkBatches.Where(candidate => candidate.Id == id).ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.OriginalKey, (string?)null), cancellationToken);
    }

    private async Task TryDeleteAsync(List<string> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        try
        {
            await storage.DeleteAsync(keys, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCleanupFailed(logger, exception, keys.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bulk batch {BatchId} analysed: {Pieces} piece(s), {CutOff} cut off.")]
    private static partial void LogReady(ILogger logger, Guid batchId, int pieces, int cutOff);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bulk batch {BatchId} refused by vision: {Refusal}.")]
    private static partial void LogRefused(ILogger logger, Guid batchId, string refusal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bulk batch {BatchId}: vision failed ({Reason}); asking once more in {DelayMs} ms.")]
    private static partial void LogRetry(ILogger logger, Guid batchId, string reason, int delayMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bulk batch {BatchId} failed ({Code}): {Detail}.")]
    private static partial void LogFailed(ILogger logger, Guid batchId, string code, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bulk batch {BatchId}: staging the pieces failed.")]
    private static partial void LogStagingFailed(ILogger logger, Exception exception, Guid batchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bulk batch {BatchId}: deleting the room photo failed; it goes with the batch.")]
    private static partial void LogPhotoDeleteFailed(ILogger logger, Exception exception, Guid batchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting {Count} staged object(s) of a bulk batch failed; the batch's prefix sweep removes them.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception, int count);
}
