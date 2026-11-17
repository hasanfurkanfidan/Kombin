using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.League;

/// <summary>
/// One piece of an outfit as the league shows it: the slot and an image, nothing else. To a voter the image is always the league's own copy
/// (a random key, new every day) or a catalog image. The league's promise is no name, no profile and no stable identifier in any response; it is
/// not that an outfit cannot be traced to its owner, because the picture is the owner's own photo (only the file's bytes differ) and someone who
/// knows the piece can recognise it. <c>ImageUrl</c> is null only in the owner's own view of an entry whose piece has no image any more.
/// </summary>
internal sealed record LeaguePieceResponse(OutfitSlot Slot, string? ImageUrl);

/// <summary>Builds league pieces. The only place that turns entry pieces into URLs.</summary>
internal static class LeagueResponses
{
    /// <summary>
    /// What a voter sees of the given entries, by entry ID, pieces in position order: the copy's public URL, else the catalog image. Pieces
    /// without either are left out (a competing entry has none).
    /// </summary>
    public static async Task<Dictionary<Guid, List<LeaguePieceResponse>>> ForVotersAsync(
        KombinDbContext db, IObjectStorage storage, IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken)
    {
        var pieces = await db.LeagueEntryPieces.AsNoTracking()
            .Where(piece => entryIds.Contains(piece.EntryId))
            .OrderBy(piece => piece.EntryId).ThenBy(piece => piece.Position)
            .ToListAsync(cancellationToken);

        return entryIds.ToDictionary(
            id => id,
            id => pieces
                .Where(piece => piece.EntryId == id)
                .Select(piece => (piece.Slot, Url: piece.ImageKey is { } key ? storage.GetPublicUrl(key) : piece.ImageUrl))
                .Where(piece => piece.Url is not null)
                .Select(piece => new LeaguePieceResponse(piece.Slot, piece.Url))
                .ToList());
    }

    /// <summary>
    /// What the owner sees of their own entry: the league copy when there is one, else their own wardrobe thumbnail (their own key: shown to
    /// them only) or the product image of a catalog piece.
    /// </summary>
    public static async Task<List<LeaguePieceResponse>> ForOwnerAsync(
        KombinDbContext db, IObjectStorage storage, LeagueEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var itemIds = entry.Pieces.Select(piece => piece.WardrobeItemId).OfType<Guid>().ToList();
        var productIds = entry.Pieces.Select(piece => piece.ProductId).OfType<Guid>().ToList();
        var thumbs = await db.WardrobeItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && item.UserId == entry.UserId)
            .Select(item => new { item.Id, Key = item.ImageThumbKey ?? item.ImageCutoutKey })
            .ToDictionaryAsync(item => item.Id, item => item.Key, cancellationToken);
        var images = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id) && product.PrimaryImageUrl != null)
            .ToDictionaryAsync(product => product.Id, product => product.PrimaryImageUrl!, cancellationToken);

        string? ImageOf(LeagueEntryPiece piece) =>
            piece.ImageKey is { } copy ? storage.GetPublicUrl(copy)
            : piece.ImageUrl is { } url ? url
            : piece.WardrobeItemId is { } itemId && thumbs.GetValueOrDefault(itemId) is { } own ? storage.GetPublicUrl(own)
            : piece.ProductId is { } productId ? images.GetValueOrDefault(productId)
            : null;

        return [.. entry.Pieces.OrderBy(piece => piece.Position).Select(piece => new LeaguePieceResponse(piece.Slot, ImageOf(piece)))];
    }
}
