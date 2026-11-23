using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Wardrobe.BulkConfirm;

/// <summary>
/// One ticked piece and its edits: <c>PATCH /items/{id}</c>'s own fields and rules for the category and the colours (a null field is left as the
/// analysis said, <c>clear: ["secondaryColor"]</c> empties the second colour). Everything else is edited afterwards with <c>PATCH</c>.
/// </summary>
internal sealed record BulkConfirmPiece(Guid Id, string? CategorySlug, string? PrimaryColor, string? SecondaryColor, IReadOnlyList<string>? Clear)
{
    public static readonly IReadOnlyList<string> ClearableFields = ["secondaryColor"];

    public bool Clears(string field) => Clear?.Contains(field, StringComparer.OrdinalIgnoreCase) == true;
}

/// <summary>The ticks are the list: a piece not in it is not added, and is gone with the batch.</summary>
internal sealed record BulkConfirmRequest(IReadOnlyList<BulkConfirmPiece>? Pieces);

internal static partial class BulkConfirmEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/confirm", Handle)
            .WithName("ConfirmWardrobeBulkBatch")
            .WithSummary("Adds the ticked pieces to the wardrobe; the pieces not listed are dropped with the batch.")
            .WithDescription(
                "Each listed piece becomes an ordinary wardrobe item whose id is the piece's id. A second confirm of the same batch answers 200 with the "
                + "stored result and ignores the new body. 409 BulkNotReady before Ready, BulkExpired after a cancel or expiry; 503 StorageUnavailable "
                + "commits nothing and is safe to retry.")
            .WithValidation<BulkConfirmRequest>()
            .Produces<BulkBatchResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// One transaction under the batch row lock: the copies of the staged images to the items' public keys, the items, the pieces deleted (the
    /// unticked ones too), the batch <c>Confirmed</c> with its stop-rule counters. A storage failure deletes the copies already written and rolls
    /// back. After the commit the batch's objects are deleted by their prefix and <c>bulk-add-moderate</c> is enqueued for the new items.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id,
        BulkConfirmRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IBackgroundJobClient jobs,
        IClock clock,
        IOptions<BulkAddOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;
        var listed = request.Pieces!;
        var logger = loggers.CreateLogger(typeof(BulkConfirmEndpoint));

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        BulkBatch batch;
        List<string> written = [];
        try
        {
            var locked = await BulkStaging.LockAsync(db, id, userId, cancellationToken);
            if (locked is null)
            {
                return BulkAddErrors.NotFound.ToHttpResult();
            }

            batch = locked;
            if (batch.Status == BulkBatchStatus.Confirmed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await StoredResultAsync(db, storage, options.Value, id, now, cancellationToken);
            }

            if (batch.Status is BulkBatchStatus.Cancelled or BulkBatchStatus.Expired || batch.ExpiresAt <= now)
            {
                return BulkAddErrors.Expired.ToHttpResult();
            }

            if (batch.Status != BulkBatchStatus.Ready)
            {
                return BulkAddErrors.NotReady.ToHttpResult();
            }

            var pieces = await db.BulkPieces.Where(piece => piece.BatchId == id).ToListAsync(cancellationToken);
            var byId = pieces.ToDictionary(piece => piece.Id);
            if (listed.Any(entry => !byId.ContainsKey(entry.Id)))
            {
                return BulkAddErrors.UnknownPiece.ToHttpResult();
            }

            var slugs = listed.Where(entry => entry.CategorySlug is not null).Select(entry => entry.CategorySlug!.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var categories = await db.Categories.Where(category => slugs.Contains(category.Slug)).ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);
            if (slugs.Any(slug => !categories.ContainsKey(slug)))
            {
                return WardrobeErrors.UnknownCategory.ToHttpResult();
            }

            var ticked = listed.Select(entry => (Entry: entry, Piece: byId[entry.Id])).OrderBy(pair => pair.Piece.SortOrder).ToList();
            List<WardrobeItem> items = [];
            foreach (var (entry, piece) in ticked)
            {
                var item = ToItem(piece, userId, now);
                if (!await CopyImagesAsync(storage, piece, item, written, cancellationToken))
                {
                    await DeleteCopiesAsync(storage, written, logger);
                    return BulkAddErrors.StorageUnavailable.ToHttpResult();
                }

                ApplyEdits(item, entry, entry.CategorySlug is { } slug ? categories[slug.Trim()] : null);
                items.Add(item);
            }

            db.WardrobeItems.AddRange(items);
            db.BulkPieces.RemoveRange(pieces);

            batch.Status = BulkBatchStatus.Confirmed;
            batch.ConfirmedAt = now;
            batch.EndedAt = now;
            batch.ConfirmedItemIds = [.. items.Select(item => item.Id)];
            batch.PiecesTicked = ticked.Count;
            batch.TickedPreTicked = ticked.Count(pair => pair.Piece.PreTicked);
            batch.CategoryEdits = ticked.Count(pair => pair.Entry.CategorySlug is { } slug && categories[slug.Trim()] != pair.Piece.CategoryId);
            batch.ColourEdits = ticked.Count(pair => ColourEdited(pair.Piece, pair.Entry));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && written.Count > 0)
        {
            // The database failed after copies were written: nothing is committed, so the copies have no item.
            LogCommitFailed(logger, exception, id);
            await DeleteCopiesAsync(storage, written, logger);
            throw;
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        db.ChangeTracker.Clear();
        await BulkStaging.DeleteObjectsAsync(db, storage, userId, id, now, logger, cancellationToken);
        jobs.Enqueue<BulkAddModerateJob>(job => job.RunAsync(id, CancellationToken.None));
        LogConfirmed(logger, id, batch.ConfirmedItemIds.Length, batch.PiecesShown);

        return await StoredResultAsync(db, storage, options.Value, id, now, cancellationToken);
    }

    private static async Task<IResult> StoredResultAsync(
        KombinDbContext db, IObjectStorage storage, BulkAddOptions options, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var batch = await db.BulkBatches.AsNoTracking().SingleAsync(candidate => candidate.Id == id, cancellationToken);

        return TypedResults.Ok(await BulkBatchResponses.BuildAsync(db, storage, options, batch, now, cancellationToken));
    }

    /// <summary>The single add's item, from the piece the same analysis produced: the piece's ID, <c>Ready</c>, confirmed now (the user looked at it).</summary>
    private static WardrobeItem ToItem(BulkPiece piece, Guid userId, DateTimeOffset now) => new()
    {
        Id = piece.Id,
        UserId = userId,
        CategoryId = piece.CategoryId,
        PrimaryColor = piece.PrimaryColor,
        SecondaryColor = piece.SecondaryColor,
        Pattern = piece.Pattern,
        Formality = piece.Formality,
        Seasons = piece.Seasons,
        Warnings = piece.Warnings,
        Embedding = piece.Embedding,
        EmbeddingModel = piece.EmbeddingModel,
        AnalysisJson = piece.AnalysisJson,
        Status = WardrobeItemStatus.Ready,
        ConfirmedAt = now,
    };

    /// <summary>
    /// <c>staged.png</c> and <c>stagedthumb.webp</c> to the item's public <c>cutout.png</c> and <c>thumb.webp</c>, the single add's keys, unchanged.
    /// False when storage failed or a staged image is missing; every key written so far is in <paramref name="written"/>.
    /// </summary>
    private static async Task<bool> CopyImagesAsync(IObjectStorage storage, BulkPiece piece, WardrobeItem item, List<string> written, CancellationToken cancellationToken)
    {
        try
        {
            if (piece.CutoutKey is not { } cutoutKey || piece.ThumbKey is not { } thumbKey
                || await storage.GetAsync(cutoutKey, cancellationToken) is not { } cutout
                || await storage.GetAsync(thumbKey, cancellationToken) is not { } thumb)
            {
                return false;
            }

            var cutoutTarget = StorageKeys.Build(WardrobeLimits.StorageArea, item.UserId, item.Id, ImageVariant.Cutout, "png");
            var thumbTarget = StorageKeys.Build(WardrobeLimits.StorageArea, item.UserId, item.Id, ImageVariant.Thumb, "webp");
            written.Add(cutoutTarget);
            await storage.PutAsync(cutoutTarget, cutout.Content, "image/png", cancellationToken);
            written.Add(thumbTarget);
            await storage.PutAsync(thumbTarget, thumb.Content, "image/webp", cancellationToken);

            item.ImageCutoutKey = cutoutTarget;
            item.ImageThumbKey = thumbTarget;

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task DeleteCopiesAsync(IObjectStorage storage, List<string> written, ILogger logger)
    {
        try
        {
            await storage.DeleteAsync(written, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The keys are the items' own: a retried confirm overwrites them, and nothing points at them meanwhile.
            LogCopiesLeft(logger, exception, written.Count);
        }
    }

    /// <summary><c>PATCH /items/{id}</c>'s rules for these fields (<c>UpdateItemEndpoint</c>): set what is sent, then clear what is named.</summary>
    private static void ApplyEdits(WardrobeItem item, BulkConfirmPiece entry, Guid? categoryId)
    {
        if (categoryId is { } category)
        {
            item.CategoryId = category;
        }

        if (EnumNames.TryParse<ProductColor>(entry.PrimaryColor, out var primary))
        {
            item.PrimaryColor = primary;
        }

        if (EnumNames.TryParse<ProductColor>(entry.SecondaryColor, out var secondary))
        {
            item.SecondaryColor = secondary;
        }

        if (entry.Clears("secondaryColor"))
        {
            item.SecondaryColor = null;
        }
    }

    private static bool ColourEdited(BulkPiece piece, BulkConfirmPiece entry) =>
        (EnumNames.TryParse<ProductColor>(entry.PrimaryColor, out var primary) && primary != piece.PrimaryColor)
        || (EnumNames.TryParse<ProductColor>(entry.SecondaryColor, out var secondary) && secondary != piece.SecondaryColor)
        || (entry.Clears("secondaryColor") && piece.SecondaryColor is not null);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bulk batch {BatchId} confirmed: {Ticked} of {Shown} piece(s) added.")]
    private static partial void LogConfirmed(ILogger logger, Guid batchId, int ticked, int shown);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bulk batch {BatchId}: the confirm failed after the images were copied; the copies are deleted.")]
    private static partial void LogCommitFailed(ILogger logger, Exception exception, Guid batchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting {Count} copied image(s) of a failed bulk confirm failed; a retry overwrites them.")]
    private static partial void LogCopiesLeft(ILogger logger, Exception exception, int count);
}
