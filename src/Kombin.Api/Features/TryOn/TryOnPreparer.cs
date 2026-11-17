using System.Text.Json;
using Kombin.Api.Common.Imaging;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Vision;
using Kombin.Api.Features.Catalog.Embeddings;
using Kombin.Api.Features.TryOn.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.TryOn;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Features.TryOn;

internal enum PrepareOutcome
{
    /// <summary>Not due, not preparable, or another prepare holds the row.</summary>
    Skipped,
    Prepared,

    /// <summary>A permanent refusal (or no usable photo).</summary>
    Refused,

    /// <summary>A transient failure; the row says when the next try is.</summary>
    Retry,
}

/// <summary>
/// Prepares one garment: claims its row (a lease, so two runs or a run and a request never prepare it twice), sends the photos to
/// <c>/v1/garment/prepare</c>, stores the cutout and the answer, or records the failure (<see cref="TryOnPrepareRules.AfterFailure"/>). A wardrobe
/// item sends its stored cutout; a product sends up to six gallery photos fetched through the catalogue's guarded fetcher and scaled to the
/// service's limits (<see cref="VisionImageFit"/>). Only the IDs and the service's error codes are logged.
/// </summary>
internal sealed partial class TryOnPreparer(
    KombinDbContext db,
    IObjectStorage storage,
    IVisionClient vision,
    CatalogImageFetcher fetcher,
    IOptions<TryOnOptions> options,
    ILogger<TryOnPreparer> logger)
{
    /// <summary>Keys are <c>tryon/{userId}/{garmentId}/prepared.png</c>; catalogue rows use the empty user ID.</summary>
    public const string StorageArea = "tryon";

    public const string PersonWearing = "person_wearing";

    private sealed record Photo(int GalleryIndex, byte[] Content, string ContentType);

    public async Task<PrepareOutcome> PrepareWardrobeItemAsync(Guid itemId, string? currentVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var item = await db.WardrobeItems.AsNoTracking()
            .Where(candidate => candidate.Id == itemId)
            .Select(candidate => new
            {
                candidate.UserId,
                candidate.Status,
                candidate.IsArchived,
                candidate.ImageCutoutKey,
                candidate.Warnings,
                candidate.FromCatalog,
                candidate.ProductId,
                Slug = db.Categories.Where(category => category.Id == candidate.CategoryId).Select(category => category.Slug).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        // Never a photo of someone wearing it (its cutout lost the background the service needs to tell), never a catalogue copy (the product's row serves).
        if (item is not { Status: WardrobeItemStatus.Ready, IsArchived: false, ImageCutoutKey: { } cutoutKey, Slug: { } slug }
            || TryOnFamilies.ForCategory(slug) is null
            || item.Warnings.Contains(PersonWearing)
            || (item.FromCatalog && item.ProductId is not null))
        {
            return PrepareOutcome.Skipped;
        }

        var hash = TryOnSources.Wardrobe(slug, cutoutKey);
        var row = await ClaimAsync(itemId, null, now, cancellationToken);
        if (row is null)
        {
            return PrepareOutcome.Skipped;
        }

        return await WorkAsync(row, hash, currentVersion, now, item.UserId, slug, async () =>
        {
            var cutout = await storage.GetAsync(cutoutKey, cancellationToken);
            return cutout is null ? ([], "cutout_missing", false) : ([new Photo(0, cutout.Content, cutout.ContentType)], null, false);
        }, cancellationToken);
    }

    /// <summary>The caller holds <see cref="TryOnCatalogueLock"/>: one catalogue prepare at a time.</summary>
    public async Task<PrepareOutcome> PrepareProductAsync(Guid productId, string? currentVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var slug = await db.Products.AsNoTracking().Where(product => product.Id == productId).Select(product => product.Category.Slug).SingleOrDefaultAsync(cancellationToken);
        if (slug is null || TryOnFamilies.ForCategory(slug) is null)
        {
            return PrepareOutcome.Skipped;
        }

        // Gallery order as PostgreSQL sorts it (the job's SQL hashes the same list).
        var urls = await db.ProductImages.AsNoTracking()
            .Where(image => image.ProductId == productId)
            .OrderBy(image => image.SortOrder).ThenBy(image => image.Id)
            .Select(image => image.Url)
            .Take(VisionLimits.MaxPrepareImages)
            .ToListAsync(cancellationToken);

        var hash = TryOnSources.Product(slug, urls);
        var row = await ClaimAsync(null, productId, now, cancellationToken);
        if (row is null)
        {
            return PrepareOutcome.Skipped;
        }

        return await WorkAsync(row, hash, currentVersion, now, Guid.Empty, slug, async () =>
        {
            if (urls.Count == 0)
            {
                return ([], "no_images", false);
            }

            var photos = new List<Photo>();
            var transient = false;
            for (var index = 0; index < urls.Count; index++)
            {
                var fetched = await fetcher.FetchAsync(urls[index], options.Value.MaxSourceImageBytes, cancellationToken);
                if (fetched is { Status: ImageFetchStatus.Ok, Image: { } image })
                {
                    photos.Add(new Photo(index, image.Content, image.ContentType));
                }
                else
                {
                    transient |= fetched.Status == ImageFetchStatus.Transient;
                }
            }

            return photos.Count > 0 ? (photos, null, false)
                : transient ? ([], "images_unreachable", true)
                : ([], "no_usable_images", false);
        }, cancellationToken);
    }

    /// <summary>
    /// The row of the owner with its lease taken, or null when another prepare holds it. The row is created first when there is none (a
    /// concurrent creator wins the unique index; then this one claims the same row like any other).
    /// </summary>
    private async Task<TryOnGarment?> ClaimAsync(Guid? itemId, Guid? productId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = itemId is { } item
            ? db.TryOnGarments.Where(garment => garment.WardrobeItemId == item)
            : db.TryOnGarments.Where(garment => garment.ProductId == productId);

        if (!await rows.AnyAsync(cancellationToken))
        {
            db.TryOnGarments.Add(new TryOnGarment { WardrobeItemId = itemId, ProductId = productId, Status = TryOnGarmentStatus.Pending });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Someone else created it a moment ago: claim theirs.
            }

            db.ChangeTracker.Clear();
        }

        var until = now + options.Value.Lease;
        var claimed = await rows
            .Where(garment => garment.LeaseUntil == null || garment.LeaseUntil <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(garment => garment.LeaseUntil, until), cancellationToken);

        return claimed == 0 ? null : await rows.SingleAsync(cancellationToken);
    }

    private async Task<PrepareOutcome> WorkAsync(
        TryOnGarment row,
        string hash,
        string? currentVersion,
        DateTimeOffset now,
        Guid ownerUserId,
        string slug,
        Func<Task<(List<Photo> Photos, string? Error, bool Transient)>> load,
        CancellationToken cancellationToken)
    {
        try
        {
            // Under the lease the decision is taken again: a run that listed the row a moment ago may have been overtaken.
            var state = GarmentRowState.Of(row) with { LeaseUntil = null };
            if (TryOnPrepareRules.Due(state, hash, currentVersion, now) == PrepareUrgency.None)
            {
                row.LeaseUntil = null;
                await db.SaveChangesAsync(cancellationToken);
                return PrepareOutcome.Skipped;
            }

            if (row.SourceHash != hash)
            {
                // A new source starts its own count: one later retry for it.
                row.SourceHash = hash;
                row.Attempts = 0;
            }

            var (photos, loadError, loadTransient) = await load();
            if (loadError is not null)
            {
                return await FailAsync(row, loadError, loadTransient, now, cancellationToken);
            }

            var fitted = VisionImageFit.FitRequest(
                [.. photos.Select(photo => (photo.Content, photo.ContentType))],
                VisionLimits.MaxPhotoBytes,
                VisionLimits.MaxPrepareRequestBytes,
                VisionLimits.WorkingSide,
                options.Value.MaxSourcePixels);
            if (fitted.Count == 0)
            {
                return await FailAsync(row, "images_too_large", false, now, cancellationToken);
            }

            var call = await vision.PrepareAsync([.. fitted.Select(photo => new VisionImage(photo.Content, photo.ContentType))], slug, cancellationToken);
            if (call.Value is not { } answer)
            {
                return await FailAsync(row, call.Error ?? "vision_failed", call.IsTransient, now, cancellationToken);
            }

            byte[] cutout;
            try
            {
                cutout = Convert.FromBase64String(answer.Cutout);
            }
            catch (FormatException)
            {
                return await FailAsync(row, "vision_bad_response: cutout is not base64", true, now, cancellationToken);
            }

            var key = StorageKeys.Build(StorageArea, ownerUserId, row.Id, ImageVariant.Prepared, "png");
            await storage.PutAsync(key, cutout, "image/png", cancellationToken);

            var used = answer.ImageIndex >= 0 && answer.ImageIndex < fitted.Count ? photos[fitted[answer.ImageIndex].SourceIndex].GalleryIndex : 0;

            row.Status = TryOnGarmentStatus.Prepared;
            row.PreparedHash = hash;
            row.PrepareVersion = Clip(answer.Version, 40);
            row.Family = Clip(answer.Family, 20);
            row.Source = Clip(answer.Source, 20);
            row.Reliable = answer.Reliable;
            row.MayBeOutermost = answer.MayBeOutermost;
            row.SleevesJson = answer.Sleeves is { ValueKind: not JsonValueKind.Null } sleeves ? sleeves.GetRawText() : null;
            row.Flags = [.. answer.Flags];
            row.LandmarksJson = answer.Landmarks.ValueKind is JsonValueKind.Object ? answer.Landmarks.GetRawText() : "{}";
            row.CutoutKey = key;
            row.Width = answer.Width;
            row.Height = answer.Height;
            row.ImageIndex = used;
            row.PreparedAt = now;
            row.Attempts = 0;
            row.NextAttemptAt = null;
            row.RequestedAt = null;
            row.LastError = null;
            row.LeaseUntil = null;
            await db.SaveChangesAsync(cancellationToken);

            LogPrepared(logger, row.Id, answer.Family, answer.Reliable);

            return PrepareOutcome.Prepared;
        }
        finally
        {
            // Whatever happened above (an exception included), the row is free again.
            if (row.LeaseUntil is not null)
            {
                await db.TryOnGarments.Where(garment => garment.Id == row.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(garment => garment.LeaseUntil, (DateTimeOffset?)null), CancellationToken.None);
            }
        }
    }

    private async Task<PrepareOutcome> FailAsync(TryOnGarment row, string error, bool transient, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outcome = TryOnPrepareRules.AfterFailure(
            row.PreparedAt is not null, row.Attempts, transient, now, options.Value.RetryAfter, options.Value.FailedRetryAfter);

        row.Status = outcome.Status;
        row.Attempts = outcome.Attempts;
        row.NextAttemptAt = outcome.NextAttemptAt;
        row.LastError = Clip(error, TryOnGarment.MaxErrorLength);
        row.LeaseUntil = null;
        await db.SaveChangesAsync(cancellationToken);

        LogFailed(logger, row.Id, row.LastError, outcome.Status);

        return transient ? PrepareOutcome.Retry : PrepareOutcome.Refused;
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    [LoggerMessage(Level = LogLevel.Information, Message = "Try-on garment {GarmentId} prepared: {Family}, reliable {Reliable}.")]
    private static partial void LogPrepared(ILogger logger, Guid garmentId, string family, bool reliable);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Try-on garment {GarmentId}: prepare failed ({Reason}); now {Status}.")]
    private static partial void LogFailed(ILogger logger, Guid garmentId, string reason, TryOnGarmentStatus status);
}
