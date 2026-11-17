using Kombin.Api.Features.TryOn.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.TryOn;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.TryOn;

/// <summary>A piece of the outfit as the outfit names it: a wardrobe item of the caller or a catalogue product.</summary>
internal sealed record PieceRef(Guid? WardrobeItemId, Guid? ProductId)
{
    public Guid Id => WardrobeItemId ?? ProductId!.Value;
}

/// <summary>
/// One piece with what try-on needs: the garment row's owner (<c>OwnerItemId</c> for the caller's own photo, <c>OwnerProductId</c> for a
/// product, and for a wardrobe item added from the catalogue: its product), what is sent with it (size, cut, brand and category for the size
/// hint) and its readiness facts.
/// </summary>
internal sealed record TryOnPiece(
    PieceRef Ref,
    Guid? OwnerItemId,
    Guid? OwnerProductId,
    string? CategorySlug,
    Guid? BrandId,
    string? Size,
    ProductFit Fit,
    TryOnGarment? Row,
    TryOnPieceFacts Facts);

/// <summary>Loads an outfit's pieces and their garment rows in a few queries.</summary>
internal static class TryOnPieces
{
    public static async Task<List<TryOnPiece>> LoadAsync(KombinDbContext db, Guid userId, IReadOnlyList<PieceRef> refs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refs);

        var itemIds = refs.Where(piece => piece.WardrobeItemId is not null).Select(piece => piece.WardrobeItemId!.Value).Distinct().ToList();
        var items = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && itemIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.Status,
                item.IsArchived,
                item.ImageCutoutKey,
                item.Warnings,
                item.FromCatalog,
                item.ProductId,
                item.Size,
                item.Fit,
                item.BrandId,
                Slug = db.Categories.Where(category => category.Id == item.CategoryId).Select(category => category.Slug).FirstOrDefault(),
            })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var productIds = refs.Where(piece => piece.ProductId is not null).Select(piece => piece.ProductId!.Value)
            .Concat(items.Values.Where(item => item.FromCatalog && item.ProductId is not null).Select(item => item.ProductId!.Value))
            .Distinct()
            .ToList();
        var products = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new
            {
                product.Id,
                product.BrandId,
                product.Fit,
                product.Category.Slug,
                HasImages = db.ProductImages.Any(image => image.ProductId == product.Id),
            })
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var rows = await db.TryOnGarments.AsNoTracking()
            .Where(garment => (garment.WardrobeItemId != null && itemIds.Contains(garment.WardrobeItemId.Value))
                || (garment.ProductId != null && productIds.Contains(garment.ProductId.Value)))
            .ToListAsync(cancellationToken);
        var itemRows = rows.Where(row => row.WardrobeItemId is not null).ToDictionary(row => row.WardrobeItemId!.Value);
        var productRows = rows.Where(row => row.ProductId is not null).ToDictionary(row => row.ProductId!.Value);

        var pieces = new List<TryOnPiece>();
        foreach (var piece in refs)
        {
            if (piece.WardrobeItemId is { } itemId)
            {
                if (!items.TryGetValue(itemId, out var item) || item.IsArchived || item.Status != WardrobeItemStatus.Ready)
                {
                    // Gone, archived or not analysed: never drawn.
                    pieces.Add(new TryOnPiece(piece, itemId, null, item?.Slug, item?.BrandId, item?.Size, item?.Fit ?? ProductFit.Unknown, null,
                        new TryOnPieceFacts(piece.Id, TryOnFamilies.ForCategory(item?.Slug), TryOnPieceState.NotReady)));
                    continue;
                }

                if (item.FromCatalog && item.ProductId is { } sourceProduct)
                {
                    var product = products.GetValueOrDefault(sourceProduct);
                    var row = productRows.GetValueOrDefault(sourceProduct);
                    pieces.Add(new TryOnPiece(piece, null, sourceProduct, item.Slug ?? product?.Slug, item.BrandId ?? product?.BrandId, item.Size,
                        item.Fit != ProductFit.Unknown ? item.Fit : product?.Fit ?? ProductFit.Unknown, row,
                        Facts(piece.Id, item.Slug ?? product?.Slug, row, preparable: product?.HasImages == true, personInPhoto: false)));
                    continue;
                }

                var own = itemRows.GetValueOrDefault(itemId);
                pieces.Add(new TryOnPiece(piece, itemId, null, item.Slug, item.BrandId, item.Size, item.Fit, own,
                    Facts(piece.Id, item.Slug, own, preparable: item.ImageCutoutKey is not null, personInPhoto: item.Warnings.Contains(TryOnPreparer.PersonWearing))));
                continue;
            }

            var productId = piece.ProductId!.Value;
            var catalogue = products.GetValueOrDefault(productId);
            var productRow = productRows.GetValueOrDefault(productId);
            pieces.Add(new TryOnPiece(piece, null, productId, catalogue?.Slug, catalogue?.BrandId, null, catalogue?.Fit ?? ProductFit.Unknown, productRow,
                Facts(piece.Id, catalogue?.Slug, productRow, preparable: catalogue?.HasImages == true, personInPhoto: false)));
        }

        return pieces;
    }

    private static TryOnPieceFacts Facts(Guid pieceId, string? slug, TryOnGarment? row, bool preparable, bool personInPhoto)
    {
        var family = TryOnFamilies.Parse(row?.Family) ?? TryOnFamilies.ForCategory(slug);

        if (personInPhoto)
        {
            return new TryOnPieceFacts(pieceId, family, TryOnPieceState.PersonInPhoto);
        }

        return row?.Status switch
        {
            TryOnGarmentStatus.Prepared => new TryOnPieceFacts(pieceId, family, TryOnPieceState.Prepared, row.Reliable, row.MayBeOutermost),
            TryOnGarmentStatus.Refused or TryOnGarmentStatus.Failed => new TryOnPieceFacts(pieceId, family, TryOnPieceState.NotReady),
            _ when !preparable || family is null => new TryOnPieceFacts(pieceId, family, TryOnPieceState.NotReady),
            TryOnGarmentStatus.Pending => new TryOnPieceFacts(pieceId, family, TryOnPieceState.Pending),
            _ => new TryOnPieceFacts(pieceId, family, TryOnPieceState.NoRow),
        };
    }
}
