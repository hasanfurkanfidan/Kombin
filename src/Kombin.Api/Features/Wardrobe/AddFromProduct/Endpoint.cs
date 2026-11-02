using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
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
    /// and no resizing). If the copy fails the item is still created and shows the catalog image. Adding a product that is already in
    /// the wardrobe returns that item (200) instead of a second one.
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

        var existing = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && item.ProductId == product.Id && !item.IsArchived)
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return TypedResults.Ok(await WardrobeItemResponses.BuildAsync(db, storage, existing, cancellationToken));
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
        };

        if (product.PrimaryImageUrl is { } imageUrl)
        {
            var logger = loggers.CreateLogger(typeof(AddFromProductEndpoint));
            var image = await CatalogImages.DownloadAsync(httpClients.CreateClient(WardrobeModule.CatalogImageClient), imageUrl, logger, cancellationToken);

            if (image is not null)
            {
                var extension = StorageKeys.ExtensionFor(image.ContentType)!;
                var cutoutKey = StorageKeys.Build(WardrobeLimits.StorageArea, userId, item.Id, ImageVariant.Cutout, extension);
                var thumbKey = StorageKeys.Build(WardrobeLimits.StorageArea, userId, item.Id, ImageVariant.Thumb, extension);

                await storage.PutAsync(cutoutKey, image.Content, image.ContentType, cancellationToken);
                await storage.PutAsync(thumbKey, image.Content, image.ContentType, cancellationToken);

                item.ImageCutoutKey = cutoutKey;
                item.ImageThumbKey = thumbKey;
            }
        }

        db.WardrobeItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/v1/me/wardrobe/items/{item.Id}", await WardrobeItemResponses.BuildAsync(db, storage, item, cancellationToken));
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
}
