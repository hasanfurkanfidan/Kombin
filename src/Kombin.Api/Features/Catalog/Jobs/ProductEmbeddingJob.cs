using System.Diagnostics;
using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog.Embeddings;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Kombin.Api.Features.Catalog.Jobs;

/// <summary>
/// <c>Due</c> is how many products needed work; <c>Remaining</c> how many of them this run left for the next (limit or time budget).
/// <c>Aborted</c>: the vision service could not be asked or failed <see cref="ProductEmbeddingRules.MaxConsecutiveVisionFailures"/> times in a row.
/// </summary>
internal readonly record struct EmbeddingRunSummary(int Due, int Embedded, int Reused, int Unusable, int Failed, int Remaining, bool Aborted);

/// <summary>
/// Computes the visual-search vector of catalog products (<see cref="ProductEmbedding"/>): for every active product that is due
/// (<see cref="ProductEmbeddingRules.IsDue"/>: no row yet, a new image URL, a new vision model, a failed attempt whose back-off is over), it
/// downloads the primary image politely (<see cref="CatalogImageFetcher"/>: <c>KombinBot</c>, one request per host per second, hosts
/// interleaved) and asks <c>kombin-vision /v1/embed</c> (no background removal: catalog images are clean product shots). A product whose
/// image another product already embedded under the same URL and model reuses that vector without a download. Every product is committed on
/// its own, so a run that stops (time budget, cancellation, a crash) loses nothing and the next run carries on: resumable and idempotent.
/// Nightly, plus <c>POST /internal/catalog/embeddings/run</c> for a backfill on demand. One vision call at a time, so a user's wardrobe photo
/// always finds the vision service's second slot free.
/// </summary>
internal sealed partial class ProductEmbeddingJob(
    KombinDbContext db,
    IVisionClient vision,
    IHttpClientFactory httpClients,
    IOptions<CatalogEmbeddingOptions> options,
    IClock clock,
    ILogger<ProductEmbeddingJob> logger)
{
    public const string JobId = "product-embeddings";

    /// <summary>Daily at 03:00 in Europe/Istanbul (the zone is set where the job is registered, in <c>UseCatalogJobs</c>), before the offer sweep.</summary>
    public const string Cron = "0 3 * * *";

    /// <summary>The named client that downloads catalog images (user agent and timeout); tests swap its handler.</summary>
    public const string ImageClient = "catalog-embedding-images";

    private enum Outcome
    {
        Embedded,
        Reused,
        Unusable,
        Failed,
        VisionFailed,
    }

    private sealed record Candidate(Guid ProductId, string Url, Guid? ImageId, EmbeddingState? Row);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await EmbedAsync(null, int.MaxValue, options.Value.MaxRunDuration, cancellationToken);

        LogRun(logger, summary.Due, summary.Embedded, summary.Reused, summary.Unusable, summary.Failed, summary.Remaining, summary.Aborted);
    }

    /// <summary>
    /// Works on at most <paramref name="limit"/> due products for at most <paramref name="budget"/>. <paramref name="productIds"/> restricts it to
    /// those products (tests: the database is shared; the internal endpoint).
    /// </summary>
    public async Task<EmbeddingRunSummary> EmbedAsync(IReadOnlyCollection<Guid>? productIds, int limit, TimeSpan? budget, CancellationToken cancellationToken)
    {
        var model = await vision.GetEmbeddingModelAsync(cancellationToken);
        if (model is null)
        {
            LogNoModel(logger);
            return new EmbeddingRunSummary(0, 0, 0, 0, 0, 0, Aborted: true);
        }

        var now = clock.UtcNow;
        var due = (await LoadCandidatesAsync(productIds, cancellationToken))
            .Where(candidate => ProductEmbeddingRules.IsDue(candidate.Row, candidate.Url, model, now))
            .OrderBy(candidate => candidate.Row is null ? 0 : 1)
            .ThenBy(candidate => candidate.ProductId)
            .ToList();
        var work = PoliteSchedule.Interleave(due.Take(limit), candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty);

        var fetcher = new CatalogImageFetcher(httpClients.CreateClient(ImageClient), new PoliteSchedule(options.Value.HostInterval), TimeProvider.System);
        var stopwatch = Stopwatch.StartNew();
        int embedded = 0, reused = 0, unusable = 0, failed = 0, processed = 0, visionFailuresInARow = 0;
        var aborted = false;

        foreach (var candidate in work)
        {
            if (budget is { } maximum && stopwatch.Elapsed >= maximum)
            {
                break;
            }

            var outcome = await EmbedOneAsync(candidate, model, fetcher, cancellationToken);
            processed++;
            visionFailuresInARow = outcome == Outcome.VisionFailed ? visionFailuresInARow + 1 : 0;

            switch (outcome)
            {
                case Outcome.Embedded:
                    embedded++;
                    break;
                case Outcome.Reused:
                    reused++;
                    break;
                case Outcome.Unusable:
                    unusable++;
                    break;
                default:
                    failed++;
                    break;
            }

            if (visionFailuresInARow >= ProductEmbeddingRules.MaxConsecutiveVisionFailures)
            {
                aborted = true;
                LogVisionDown(logger, visionFailuresInARow);
                break;
            }
        }

        return new EmbeddingRunSummary(due.Count, embedded, reused, unusable, failed, due.Count - processed, aborted);
    }

    /// <summary>Every active product with an image, its source image and its row's state: one query, the due rule is applied in memory.</summary>
    private async Task<List<Candidate>> LoadCandidatesAsync(IReadOnlyCollection<Guid>? productIds, CancellationToken cancellationToken)
    {
        var products = db.Products.AsNoTracking().Where(product => product.IsActive);
        if (productIds is not null)
        {
            products = products.Where(product => productIds.Contains(product.Id));
        }

        var rows = await products
            .Select(product => new
            {
                product.Id,
                product.PrimaryImageUrl,
                PrimaryImageId = product.Images.Where(image => image.Url == product.PrimaryImageUrl).Select(image => (Guid?)image.Id).FirstOrDefault(),
                First = product.Images.OrderBy(image => image.SortOrder).ThenBy(image => image.Id).Select(image => new { image.Id, image.Url }).FirstOrDefault(),
                Row = db.ProductEmbeddings
                    .Where(embedding => embedding.ProductId == product.Id)
                    .Select(embedding => new EmbeddingState(embedding.Status, embedding.Model, embedding.SourceImageUrl, embedding.NextAttemptAt, embedding.CheckedAt))
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var candidates = new List<Candidate>(rows.Count);
        foreach (var row in rows)
        {
            var url = ProductEmbeddingRules.SourceImage(row.PrimaryImageUrl, row.First?.Url);
            if (url is null)
            {
                continue;
            }

            var imageId = url == row.PrimaryImageUrl ? row.PrimaryImageId : row.First?.Id;
            candidates.Add(new Candidate(row.Id, url, imageId, row.Row));
        }

        return candidates;
    }

    private async Task<Outcome> EmbedOneAsync(Candidate candidate, string model, CatalogImageFetcher fetcher, CancellationToken cancellationToken)
    {
        var row = await db.ProductEmbeddings.SingleOrDefaultAsync(embedding => embedding.ProductId == candidate.ProductId, cancellationToken);
        if (row is null)
        {
            row = new ProductEmbedding { ProductId = candidate.ProductId, Model = model };
            db.ProductEmbeddings.Add(row);
        }

        row.SourceImageUrl = candidate.Url;
        row.SourceImageId = candidate.ImageId;
        row.CheckedAt = clock.UtcNow;

        Outcome outcome;
        var shared = await db.ProductEmbeddings.AsNoTracking()
            .Where(embedding => embedding.SourceImageUrl == candidate.Url
                && embedding.ProductId != candidate.ProductId
                && embedding.Status == ProductEmbeddingStatus.Ready
                && embedding.Model == model)
            .Select(embedding => embedding.Embedding)
            .FirstOrDefaultAsync(cancellationToken);

        if (shared is not null)
        {
            MarkReady(row, shared, model);
            outcome = Outcome.Reused;
        }
        else
        {
            outcome = await DownloadAndEmbedAsync(row, model, fetcher, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // The product was deleted while the run was on it (the row's foreign key), or another run inserted its row first: skip it.
            LogSaveSkipped(logger, candidate.ProductId, exception.InnerException?.Message ?? exception.Message);
            outcome = Outcome.Failed;
        }

        // One product per unit of work: keep the change tracker from growing over a run of thousands.
        db.ChangeTracker.Clear();

        return outcome;
    }

    private async Task<Outcome> DownloadAndEmbedAsync(ProductEmbedding row, string model, CatalogImageFetcher fetcher, CancellationToken cancellationToken)
    {
        var fetch = await fetcher.FetchAsync(row.SourceImageUrl, cancellationToken);
        switch (fetch.Status)
        {
            case ImageFetchStatus.NotFound or ImageFetchStatus.Unusable:
                MarkUnusable(row, fetch.Reason ?? "image_unusable", model);
                return Outcome.Unusable;
            case ImageFetchStatus.Transient:
                MarkFailed(row, fetch.Reason ?? "image_unreachable");
                return Outcome.Failed;
        }

        var image = fetch.Image!;
        var result = await vision.EmbedAsync(image.Content, image.ContentType, cancellationToken);
        if (result.Value is not { } answer)
        {
            if (result.IsTransient)
            {
                MarkFailed(row, result.Error ?? "vision_failed");
                return Outcome.VisionFailed;
            }

            MarkUnusable(row, result.Error ?? "vision_refused", model);
            return Outcome.Unusable;
        }

        if (answer.Embedding.Length != ProductEmbedding.Dimensions)
        {
            MarkFailed(row, $"vision_bad_response: embedding has {answer.Embedding.Length} dimensions");
            return Outcome.VisionFailed;
        }

        MarkReady(row, new Vector(answer.Embedding), model);
        return Outcome.Embedded;
    }

    private static void MarkReady(ProductEmbedding row, Vector embedding, string model)
    {
        row.Embedding = embedding;
        row.Model = model;
        row.Status = ProductEmbeddingStatus.Ready;
        row.Attempts = 0;
        row.FailureReason = null;
        row.NextAttemptAt = null;
    }

    /// <summary>The image is gone or refused: the vector goes too (the product page has no usable image either).</summary>
    private static void MarkUnusable(ProductEmbedding row, string reason, string model)
    {
        row.Embedding = null;
        row.Model = model;
        row.Status = ProductEmbeddingStatus.Unusable;
        row.Attempts = 0;
        row.FailureReason = Clip(reason);
        row.NextAttemptAt = null;
    }

    /// <summary>A transient failure keeps the previous vector and its model, if any, until a later attempt succeeds.</summary>
    private void MarkFailed(ProductEmbedding row, string reason)
    {
        row.Status = ProductEmbeddingStatus.Failed;
        row.Attempts++;
        row.FailureReason = Clip(reason);
        row.NextAttemptAt = ProductEmbeddingRules.NextAttempt(row.Attempts, clock.UtcNow);
    }

    private static string Clip(string reason) => reason.Length <= 300 ? reason : reason[..300];

    [LoggerMessage(Level = LogLevel.Information, Message = "Product embeddings: {Due} due, {Embedded} embedded, {Reused} reused, {Unusable} unusable, {Failed} failed, {Remaining} left for the next run, aborted {Aborted}.")]
    private static partial void LogRun(ILogger logger, int due, int embedded, int reused, int unusable, int failed, int remaining, bool aborted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Product embeddings skipped: the vision service did not say which model it runs.")]
    private static partial void LogNoModel(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Product embeddings stopped after {Failures} vision failures in a row.")]
    private static partial void LogVisionDown(ILogger logger, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Product {ProductId}: embedding not saved ({Reason}).")]
    private static partial void LogSaveSkipped(ILogger logger, Guid productId, string reason);
}
