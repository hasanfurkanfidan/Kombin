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
                + "stored result and ignores the new body. 409 BulkNotReady before Ready, BulkExpired after a cancel or expiry, BulkStagedImageMissing "
                + "when a listed piece's staged image is gone (not retryable: cancel and add the photo again); 503 StorageUnavailable commits nothing "
                + "and is safe to retry.")
            .WithValidation<BulkConfirmRequest>()
            .Produces<BulkBatchResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// One transaction under the batch row lock: the copies of the staged images to the items' public keys, the items, the pieces deleted (the
    /// unticked ones too), the batch <c>Confirmed</c> with its stop-rule counters. Any failure after a copy was written, an aborted request
    /// included, deletes the copies already written and rolls back; copies that delete cannot remove are handed to <see cref="BulkAddDiscardCopiesJob"/>.
    /// After the commit <c>bulk-add-moderate</c> is enqueued for the new items, then the batch's objects are deleted by their prefix.
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
        List<Guid> copied = [];
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
                var outcome = await CopyImagesAsync(storage, piece, item, copied, cancellationToken);
                if (outcome != CopyOutcome.Copied)
                {
                    await DiscardCopiesAsync(storage, jobs, id, userId, copied, logger);
                    return outcome == CopyOutcome.StagedImageMissing
                        ? BulkAddErrors.StagedImageMissing.ToHttpResult()
                        : BulkAddErrors.StorageUnavailable.ToHttpResult();
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

            // Never cancelled: an aborted commit could have committed, and the copies would then be deleted from under their items.
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception) when (copied.Count > 0)
        {
            // The database failed or the client went away after copies were written (the copy loop or SaveChanges): nothing is committed, so
            // the copies have no item. An abort is not logged as an error.
            if (exception is not OperationCanceledException)
            {
                LogCommitFailed(logger, exception, id);
            }

            await DiscardCopiesAsync(storage, jobs, id, userId, copied, logger);
            throw;
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        // Before anything the request's token can stop: an abort after the commit must not leave the items to the daily moderation retry.
        jobs.Enqueue<BulkAddModerateJob>(job => job.RunAsync(id, CancellationToken.None));
        LogConfirmed(logger, id, batch.ConfirmedItemIds.Length, batch.PiecesShown);
        db.ChangeTracker.Clear();
        await BulkStaging.DeleteObjectsAsync(db, storage, userId, id, now, logger, cancellationToken);

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
    /// A staged image that is not there (no key, or the store has no such object) is <see cref="CopyOutcome.StagedImageMissing"/>: a retry would
    /// fail the same way. Every piece whose copies may have been written is in <paramref name="copied"/>. An abort throws.
    /// </summary>
    private static async Task<CopyOutcome> CopyImagesAsync(IObjectStorage storage, BulkPiece piece, WardrobeItem item, List<Guid> copied, CancellationToken cancellationToken)
    {
        try
        {
            if (piece.CutoutKey is not { } cutoutKey || piece.ThumbKey is not { } thumbKey
                || await storage.GetAsync(cutoutKey, cancellationToken) is not { } cutout
                || await storage.GetAsync(thumbKey, cancellationToken) is not { } thumb)
            {
                return CopyOutcome.StagedImageMissing;
            }

            var targets = BulkStaging.CopyKeys(item.UserId, item.Id);
            copied.Add(item.Id);
            await storage.PutAsync(targets[0], cutout.Content, "image/png", cancellationToken);
            await storage.PutAsync(targets[1], thumb.Content, "image/webp", cancellationToken);

            item.ImageCutoutKey = targets[0];
            item.ImageThumbKey = targets[1];

            return CopyOutcome.Copied;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CopyOutcome.StorageFailed;
        }
    }

    /// <summary>
    /// Deletes the copies of a confirm that did not commit, whatever the request's token says, for at most <see cref="DiscardTimeout"/>. Copies
    /// it cannot delete are not lost: <see cref="BulkAddDiscardCopiesJob"/> gets their keys (they are under no prefix the cleanup or the account
    /// purge lists) and deletes the ones that still have no item.
    /// </summary>
    private static async Task DiscardCopiesAsync(IObjectStorage storage, IBackgroundJobClient jobs, Guid batchId, Guid userId, List<Guid> copied, ILogger logger)
    {
        if (copied.Count == 0)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(DiscardTimeout);
            await storage.DeleteAsync([.. copied.SelectMany(itemId => BulkStaging.CopyKeys(userId, itemId))], timeout.Token);
        }
        catch (Exception exception)
        {
            LogCopiesLeft(logger, exception, batchId, copied.Count);
            Guid[] itemIds = [.. copied];
            jobs.Enqueue<BulkAddDiscardCopiesJob>(job => job.RunAsync(batchId, userId, itemIds, CancellationToken.None));
        }
    }

    private static readonly TimeSpan DiscardTimeout = TimeSpan.FromSeconds(10);

    private enum CopyOutcome
    {
        Copied,
        StagedImageMissing,
        StorageFailed,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bulk batch {BatchId}: deleting the copies of {Count} piece(s) of a failed confirm failed; bulk-add-discard-copies deletes them.")]
    private static partial void LogCopiesLeft(ILogger logger, Exception exception, Guid batchId, int count);
}
