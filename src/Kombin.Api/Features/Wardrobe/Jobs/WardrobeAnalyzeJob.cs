using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Pgvector;

namespace Kombin.Api.Features.Wardrobe.Jobs;

internal enum AnalyzeOutcome
{
    /// <summary>The item is gone, archived or no longer processing; nothing was done.</summary>
    Skipped,
    Ready,
    Failed,

    /// <summary>The vision service could not be reached; the attempt is counted and Hangfire tries again.</summary>
    Retry,
}

/// <summary>
/// Analyses one uploaded photo: original from storage → <c>kombin-vision</c> → cutout and thumbnail back to storage → attributes,
/// embedding and <c>AnalysisJson</c> on the item → <c>Ready</c>. Fire-and-forget, enqueued by <c>POST /items/{id}/process</c>.
/// Idempotent: it only works on a <c>Processing</c> item and a rerun overwrites the same keys and fields. A photo that is not of a
/// garment is still <c>Ready</c>, with the <c>not_garment</c> warning. A refused image fails at once; an unreachable service is retried
/// by Hangfire, and the <see cref="MaxAttempts"/>th failed call makes the item <c>Failed</c> (counted on the item, so the outcome does not
/// depend on Hangfire's bookkeeping and the tests can drive it).
/// </summary>
internal sealed partial class WardrobeAnalyzeJob(KombinDbContext db, IObjectStorage storage, IVisionClient vision, IClock clock, ILogger<WardrobeAnalyzeJob> logger)
{
    public const string JobId = "wardrobe-analyze";

    /// <summary>The first try plus Hangfire's three retries.</summary>
    public const int MaxAttempts = 4;

    [AutomaticRetry(Attempts = MaxAttempts - 1, DelaysInSeconds = [5, 15, 30], OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [JobDisplayName("wardrobe-analyze {0}")]
    public async Task RunAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (await AnalyzeAsync(itemId, cancellationToken) == AnalyzeOutcome.Retry)
        {
            // Throwing is how a Hangfire job asks to be retried; the item stays Processing meanwhile.
            throw new InvalidOperationException($"The vision service was not reachable for wardrobe item {itemId}; the job will be retried.");
        }
    }

    public async Task<AnalyzeOutcome> AnalyzeAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.WardrobeItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is not { Status: WardrobeItemStatus.Processing, IsArchived: false } || item.ImageOriginalKey is null)
        {
            return AnalyzeOutcome.Skipped;
        }

        var original = await storage.GetAsync(item.ImageOriginalKey, cancellationToken);
        if (original is null)
        {
            return await FailAsync(item, "original_missing", cancellationToken);
        }

        if (original.Content.LongLength > WardrobeLimits.MaxImageBytes)
        {
            return await FailAsync(item, "image_too_large", cancellationToken);
        }

        var result = await vision.AnalyzeAsync(original.Content, original.ContentType, cancellationToken);
        if (result.Analysis is not { } analysis)
        {
            var error = result.Error ?? "vision_failed";

            if (!result.IsTransient)
            {
                return await FailAsync(item, error, cancellationToken);
            }

            item.AnalysisAttempts++;
            if (item.AnalysisAttempts >= MaxAttempts)
            {
                return await FailAsync(item, error, cancellationToken);
            }

            // Kept while the item is still processing, for whoever looks at a stuck item.
            item.FailureReason = Clip(error);
            await db.SaveChangesAsync(cancellationToken);
            LogRetry(logger, itemId, item.AnalysisAttempts, error);

            return AnalyzeOutcome.Retry;
        }

        byte[] cutout;
        byte[] thumbnail;
        try
        {
            cutout = Convert.FromBase64String(analysis.Cutout);
            thumbnail = Convert.FromBase64String(analysis.Thumbnail);
        }
        catch (FormatException)
        {
            return await FailAsync(item, "vision_bad_response: images are not base64", cancellationToken);
        }

        if (analysis.Embedding.Length != WardrobeItem.EmbeddingDimensions)
        {
            return await FailAsync(item, $"vision_bad_response: embedding has {analysis.Embedding.Length} dimensions", cancellationToken);
        }

        // Same keys on every run, so a rerun overwrites instead of leaving orphans.
        var cutoutKey = StorageKeys.Build(WardrobeLimits.StorageArea, item.UserId, item.Id, ImageVariant.Cutout, "png");
        var thumbKey = StorageKeys.Build(WardrobeLimits.StorageArea, item.UserId, item.Id, ImageVariant.Thumb, "webp");
        await storage.PutAsync(cutoutKey, cutout, "image/png", cancellationToken);
        await storage.PutAsync(thumbKey, thumbnail, "image/webp", cancellationToken);

        var attributes = WardrobeAnalysis.Map(analysis);
        var categoryId = attributes.CategorySlug is { } slug
            ? await db.Categories.Where(category => category.Slug == slug).Select(category => (Guid?)category.Id).SingleOrDefaultAsync(cancellationToken)
            : null;
        var model = await vision.GetEmbeddingModelAsync(cancellationToken);

        item.ImageCutoutKey = cutoutKey;
        item.ImageThumbKey = thumbKey;
        item.CategoryId = categoryId;
        item.PrimaryColor = attributes.PrimaryColor;
        item.SecondaryColor = attributes.SecondaryColor;
        item.Pattern = attributes.Pattern;
        item.Formality = categoryId is null ? item.Formality : attributes.Formality;
        item.Seasons = categoryId is null ? item.Seasons : attributes.Seasons;
        item.Warnings = attributes.Warnings;
        item.Embedding = new Vector(analysis.Embedding);
        item.EmbeddingModel = model;
        item.AnalysisJson = WardrobeAnalysis.ToJson(analysis, model, clock.UtcNow);
        item.Status = WardrobeItemStatus.Ready;
        item.FailureReason = null;

        await db.SaveChangesAsync(cancellationToken);
        LogReady(logger, itemId, attributes.CategorySlug ?? "(needs category)");

        return AnalyzeOutcome.Ready;
    }

    private async Task<AnalyzeOutcome> FailAsync(WardrobeItem item, string reason, CancellationToken cancellationToken)
    {
        item.Status = WardrobeItemStatus.Failed;
        item.FailureReason = Clip(reason);
        await db.SaveChangesAsync(cancellationToken);
        LogFailed(logger, item.Id, reason);

        return AnalyzeOutcome.Failed;
    }

    private static string Clip(string reason) => reason.Length <= 300 ? reason : reason[..300];

    [LoggerMessage(Level = LogLevel.Information, Message = "Wardrobe item {ItemId} analysed: {Category}.")]
    private static partial void LogReady(ILogger logger, Guid itemId, string category);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wardrobe item {ItemId}: vision attempt {Attempt} failed ({Reason}); retrying.")]
    private static partial void LogRetry(ILogger logger, Guid itemId, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wardrobe item {ItemId} failed: {Reason}.")]
    private static partial void LogFailed(ILogger logger, Guid itemId, string reason);
}
