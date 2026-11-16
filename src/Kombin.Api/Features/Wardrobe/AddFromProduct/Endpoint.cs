using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.AddFromProduct;

internal sealed record AddFromProductRequest(Guid ProductId, string? Size);

internal static class AddFromProductEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/items/from-product", Handle)
            .WithName("AddWardrobeItemFromProduct")
            .WithValidation<AddFromProductRequest>()
            .Produces<WardrobeItemResponse>(StatusCodes.Status201Created)
            .Produces<WardrobeItemResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// "Dolabıma ekle" after a purchase: a <c>Ready</c>, already confirmed item with the product's attributes. The product's primary
    /// image is copied into storage as both cutout and thumbnail, as it is (catalog shots are already clean, so no background removal
    /// and no resizing). If the download or the copy fails the item is still created, without images, and shows the catalog image (nothing is left
    /// in storage). Adding a product that is already in the wardrobe returns that item (200) instead of a second one, also for a double tap:
    /// concurrent adds of one product by one user are serialised by an advisory lock and the check is repeated under it.
    /// </summary>
    private static async Task<IResult> Handle(
        AddFromProductRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IHttpClientFactory httpClients,
        IClock clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == request.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound.ToHttpResult();
        }

        // The common repeat: no reason to fetch an image for it.
        if (await ExistingAsync(db, userId, product.Id, cancellationToken) is { } already)
        {
            return TypedResults.Ok(await WardrobeItemResponses.BuildAsync(db, storage, already, cancellationToken));
        }

        var item = new WardrobeItem
        {
            UserId = userId,
            Name = product.Name,
            CategoryId = product.CategoryId,
            PrimaryColor = product.PrimaryColor,
            Pattern = product.Pattern,
            Fit = product.Fit,
            Formality = product.Formality,
            Seasons = product.Seasons == Season.None ? Season.AllYear : product.Seasons,
            BrandId = product.BrandId,
            ProductId = product.Id,
            FromCatalog = true,
            Size = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size.Trim(),
            Status = WardrobeItemStatus.Ready,
            ConfirmedAt = clock.UtcNow,
            // The image is the retailer's catalog picture, not a user photo: shareable like any product image.
            ModerationVerdict = ModerationVerdict.Allow,
            ModeratedAt = clock.UtcNow,
        };

        var logger = loggers.CreateLogger(typeof(AddFromProductEndpoint));

        // The download is slow and holds nothing: it happens before the lock, so a double tap does not hold a connection for its length.
        var image = product.PrimaryImageUrl is { } imageUrl
            ? await CatalogImages.DownloadAsync(httpClients.CreateClient(WardrobeModule.CatalogImageClient), imageUrl, logger, cancellationToken)
            : null;

        // One add per (user, product) at a time: a transaction-scoped advisory lock, then the check again. A second tap waits here, finds the
        // first one's piece and gets it back. (A unique index would have needed the old duplicates cleaned up first; the lock needs no data change.)
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"wardrobe-from-product:{userId:N}:{product.Id:N}"}, 0))", cancellationToken);

        if (await ExistingAsync(db, userId, product.Id, cancellationToken) is { } raced)
        {
            return TypedResults.Ok(await WardrobeItemResponses.BuildAsync(db, storage, raced, cancellationToken));
        }

        var written = image is null ? [] : await CopyImageAsync(storage, item, image, logger, cancellationToken);

        try
        {
            db.WardrobeItems.Add(item);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await DeleteQuietlyAsync(storage, written, logger);
            throw;
        }

        return TypedResults.Created($"/api/v1/me/wardrobe/items/{item.Id}", await WardrobeItemResponses.BuildAsync(db, storage, item, cancellationToken));
    }

    private static Task<WardrobeItem?> ExistingAsync(KombinDbContext db, Guid userId, Guid productId, CancellationToken cancellationToken) =>
        db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && item.ProductId == productId && !item.IsArchived)
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Copies the image into storage as cutout and thumbnail and sets the keys. A store that refuses is like a download that failed: the item is
    /// created without images (it shows the catalog's) and whatever was already written is deleted, so nothing is left behind. Returns the keys written.
    /// </summary>
    private static async Task<List<string>> CopyImageAsync(IObjectStorage storage, WardrobeItem item, StoredObject image, ILogger logger, CancellationToken cancellationToken)
    {
        var extension = StorageKeys.ExtensionFor(image.ContentType)!;
        var cutoutKey = StorageKeys.Build(WardrobeLimits.StorageArea, item.UserId, item.Id, ImageVariant.Cutout, extension);
        var thumbKey = StorageKeys.Build(WardrobeLimits.StorageArea, item.UserId, item.Id, ImageVariant.Thumb, extension);
        var written = new List<string>();

        try
        {
            await storage.PutAsync(cutoutKey, image.Content, image.ContentType, cancellationToken);
            written.Add(cutoutKey);
            await storage.PutAsync(thumbKey, image.Content, image.ContentType, cancellationToken);
            written.Add(thumbKey);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CatalogImages.LogNotStored(logger, exception, item.Id);
            await DeleteQuietlyAsync(storage, [cutoutKey, thumbKey], logger);

            return [];
        }

        item.ImageCutoutKey = cutoutKey;
        item.ImageThumbKey = thumbKey;

        return written;
    }

    /// <summary>Best effort: an object that cannot be deleted now is an orphan, not a reason to fail the request.</summary>
    private static async Task DeleteQuietlyAsync(IObjectStorage storage, List<string> keys, ILogger logger)
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
            CatalogImages.LogNotDeleted(logger, exception, keys.Count);
        }
    }
}

/// <summary>Fetches a catalog image for copying; never throws for a failed download (only when the caller cancels).</summary>
internal static partial class CatalogImages
{
    /// <summary>The image, or null when it cannot be fetched, is not an accepted image type or is larger than the photo limit.</summary>
    public static async Task<StoredObject?> DownloadAsync(HttpClient http, string url, ILogger logger, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;

            if (!response.IsSuccessStatusCode
                || StorageKeys.ExtensionFor(contentType) is null
                || response.Content.Headers.ContentLength > WardrobeLimits.MaxImageBytes)
            {
                LogSkipped(logger, url, $"HTTP {(int)response.StatusCode}, {contentType ?? "no content type"}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            // The length header may be missing or wrong: stop reading at the limit either way.
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > WardrobeLimits.MaxImageBytes)
                {
                    LogSkipped(logger, url, "larger than the limit");
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return new StoredObject(buffer.ToArray(), contentType!);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogSkipped(logger, url, exception.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog image {Url} was not copied to the wardrobe: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string url, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The object store refused the catalog image of wardrobe item {ItemId}; the item is created without its own copy.")]
    internal static partial void LogNotStored(ILogger logger, Exception exception, Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Objects} stored object(s) of a wardrobe item could not be deleted and are left behind.")]
    internal static partial void LogNotDeleted(ILogger logger, Exception exception, int objects);
}
