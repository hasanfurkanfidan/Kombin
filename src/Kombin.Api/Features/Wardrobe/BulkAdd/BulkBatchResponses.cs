using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Vision;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.BulkAdd;

/// <summary>A box on the photo in fractions (0-1) of its width and height: left, top, width, height.</summary>
internal sealed record BulkBox(double X, double Y, double W, double H);

internal sealed record BulkCategoryRef(string Slug, string Name);

/// <summary>Why a piece is not pre-ticked: the service's code and the server's sentence (<see cref="BulkAddCopy"/>).</summary>
internal sealed record BulkReason(string Code, string Message);

internal sealed record BulkRefusal(string Code, string Message);

/// <summary><c>Code</c> is the <see cref="BulkFailureCode"/> name; <c>Retryable</c> says whether "tekrar dene" (<c>process</c> again) makes sense.</summary>
internal sealed record BulkFailure(string Code, string Message, bool Retryable);

/// <summary>"Dolabında benzeri var.": the user's own item this piece looks like, with its normal public thumbnail. A hint, never a block.</summary>
internal sealed record BulkLikelyDuplicate(Guid ItemId, string ThumbUrl, string Message);

/// <summary>
/// One piece of the review screen. <c>ThumbUrl</c> is the grid image (vision's 400 px thumbnail) and <c>ImageUrl</c> the review image (the cut-out as
/// vision sent it); both are private presigned links that expire at <c>UrlsExpireAt</c> (the app asks again). <c>Category</c>, the colours, the
/// pattern and <c>NeedsCategory</c> are the wardrobe item's own names and rules, so the single add's chips and pickers work unchanged.
/// </summary>
internal sealed record BulkPieceResponse(
    Guid Id,
    string ThumbUrl,
    string ImageUrl,
    DateTimeOffset UrlsExpireAt,
    BulkBox Box,
    bool PreTicked,
    IReadOnlyList<BulkReason> Reasons,
    BulkCategoryRef? Category,
    bool NeedsCategory,
    IReadOnlyList<BulkCategoryRef> CategoryCandidates,
    ProductColor PrimaryColor,
    ProductColor? SecondaryColor,
    ProductPattern Pattern,
    IReadOnlyList<string> Warnings,
    BulkLikelyDuplicate? LikelyDuplicate);

/// <summary>Garments the service saw together and could not cut apart: how many, the server's sentence and where they are.</summary>
internal sealed record BulkUnseparated(int Count, string Message, IReadOnlyList<BulkBox> Regions);

/// <summary>Pieces the service found beyond what one batch keeps: how many, and the server's sentence (it names no limit).</summary>
internal sealed record BulkCutOff(int Count, string Message);

/// <summary>
/// Everything the review screen draws (design note, section d); the app polls it every second or so while <c>Processing</c> and again to
/// refresh expired links. <c>Pieces</c> is empty unless <c>Ready</c>; <c>Items</c> is there only once <c>Confirmed</c>. Every sentence is the
/// server's (<see cref="BulkAddCopy"/>).
/// </summary>
internal sealed record BulkBatchResponse(
    Guid Id,
    BulkBatchStatus Status,
    DateTimeOffset ExpiresAt,
    BulkRefusal? Refusal,
    BulkFailure? Failure,
    IReadOnlyList<BulkPieceResponse> Pieces,
    BulkUnseparated? Unseparated,
    BulkCutOff? CutOff,
    IReadOnlyList<WardrobeItemSummary>? Items);

internal static class BulkBatchResponses
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The boxes of <see cref="BulkBatch.UnseparatedRegions"/> for the job to store.</summary>
    public static string RegionsToJson(IReadOnlyList<VisionRegionBox> regions) => JsonSerializer.Serialize(regions, Json);

    public static async Task<BulkBatchResponse> BuildAsync(
        KombinDbContext db, IObjectStorage storage, BulkAddOptions options, BulkBatch batch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(options);

        var refusal = batch is { Status: BulkBatchStatus.Refused, Refusal: { } code } ? new BulkRefusal(code, BulkAddCopy.ForRefusal(code)) : null;
        var failure = batch is { Status: BulkBatchStatus.Failed, FailureCode: { } failureCode }
            ? new BulkFailure(failureCode.ToString(), BulkAddCopy.ForFailure(failureCode), BulkAddCopy.IsRetryable(failureCode))
            : null;

        var ready = batch.Status == BulkBatchStatus.Ready;
        var pieces = ready ? await PiecesAsync(db, storage, options, batch, now, cancellationToken) : [];

        var unseparated = ready && batch.UnseparatedCount > 0
            ? new BulkUnseparated(batch.UnseparatedCount, BulkAddCopy.Unseparated(batch.UnseparatedCount), ReadRegions(batch.UnseparatedRegions))
            : null;
        var cutOff = ready && batch.CutOffCount > 0 ? new BulkCutOff(batch.CutOffCount, BulkAddCopy.CutOff(batch.CutOffCount)) : null;

        IReadOnlyList<WardrobeItemSummary>? items = null;
        if (batch.Status == BulkBatchStatus.Confirmed)
        {
            var ids = batch.ConfirmedItemIds;
            var rows = await db.WardrobeItems.AsNoTracking().Where(item => item.UserId == batch.UserId && ids.Contains(item.Id)).ToListAsync(cancellationToken);
            var summaries = await WardrobeItemResponses.SummarizeAsync(db, storage, rows, cancellationToken);
            var order = ids.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
            items = [.. summaries.OrderBy(summary => order[summary.Id])];
        }

        return new BulkBatchResponse(batch.Id, batch.Status, batch.ExpiresAt, refusal, failure, pieces, unseparated, cutOff, items);
    }

    private static async Task<List<BulkPieceResponse>> PiecesAsync(
        KombinDbContext db, IObjectStorage storage, BulkAddOptions options, BulkBatch batch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Without the embedding: 512 floats a piece the screen never needs.
        var rows = await db.BulkPieces.AsNoTracking()
            .Where(piece => piece.BatchId == batch.Id && piece.UserId == batch.UserId)
            .OrderBy(piece => piece.SortOrder)
            .Select(piece => new
            {
                piece.Id,
                piece.BoxX,
                piece.BoxY,
                piece.BoxW,
                piece.BoxH,
                piece.PreTicked,
                piece.Reasons,
                piece.CategoryId,
                piece.PrimaryColor,
                piece.SecondaryColor,
                piece.Pattern,
                piece.Warnings,
                piece.AnalysisJson,
                piece.CutoutKey,
                piece.ThumbKey,
                piece.SimilarItemId,
            })
            .ToListAsync(cancellationToken);

        var candidateSlugs = rows
            .SelectMany(row => WardrobeAnalysis.Read(row.AnalysisJson)?.CategoryCandidates.Select(candidate => candidate.Slug) ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var categoryIds = rows.Select(row => row.CategoryId).OfType<Guid>().Distinct().ToList();
        var categories = await db.Categories.AsNoTracking()
            .Where(category => categoryIds.Contains(category.Id) || candidateSlugs.Contains(category.Slug))
            .Select(category => new { category.Id, category.Slug, category.Name })
            .ToListAsync(cancellationToken);
        var categoryById = categories.ToDictionary(category => category.Id, category => new BulkCategoryRef(category.Slug, category.Name));
        var categoryBySlug = categories.ToDictionary(category => category.Slug, category => new BulkCategoryRef(category.Slug, category.Name), StringComparer.Ordinal);

        // The hint was stored when the job ran: look again, the item may be archived or gone since. Only the caller's own, ready items count.
        var similarIds = rows.Select(row => row.SimilarItemId).OfType<Guid>().Distinct().ToList();
        var similarThumbs = similarIds.Count == 0
            ? []
            : await db.WardrobeItems.AsNoTracking()
                .Where(item => item.UserId == batch.UserId && similarIds.Contains(item.Id) && item.Status == WardrobeItemStatus.Ready && !item.IsArchived && item.ImageThumbKey != null)
                .ToDictionaryAsync(item => item.Id, item => item.ImageThumbKey!, cancellationToken);

        var expiresAt = now + options.StagedUrlLifetime;

        return
        [
            .. rows.Select(row => new BulkPieceResponse(
                row.Id,
                storage.GetPresignedDownloadUrl(row.ThumbKey ?? string.Empty, options.StagedUrlLifetime),
                storage.GetPresignedDownloadUrl(row.CutoutKey ?? string.Empty, options.StagedUrlLifetime),
                expiresAt,
                new BulkBox(row.BoxX, row.BoxY, row.BoxW, row.BoxH),
                row.PreTicked,
                [.. row.Reasons.Select(reason => new BulkReason(reason, BulkAddCopy.ForReason(reason)))],
                row.CategoryId is { } categoryId ? categoryById.GetValueOrDefault(categoryId) : null,
                row.CategoryId is null,
                [.. (WardrobeAnalysis.Read(row.AnalysisJson)?.CategoryCandidates ?? []).Select(candidate => categoryBySlug.GetValueOrDefault(candidate.Slug)).OfType<BulkCategoryRef>()],
                row.PrimaryColor,
                row.SecondaryColor,
                row.Pattern,
                row.Warnings,
                row.SimilarItemId is { } similarId && similarThumbs.TryGetValue(similarId, out var thumbKey)
                    ? new BulkLikelyDuplicate(similarId, storage.GetPublicUrl(thumbKey), BulkAddCopy.LikelyDuplicate)
                    : null)),
        ];
    }

    private static List<BulkBox> ReadRegions(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return [.. (JsonSerializer.Deserialize<List<VisionRegionBox>>(json, Json) ?? []).Select(box => new BulkBox(box.X, box.Y, box.W, box.H))];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
