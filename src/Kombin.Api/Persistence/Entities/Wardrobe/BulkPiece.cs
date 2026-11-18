using Kombin.Api.Persistence.Entities.Catalog;
using Pgvector;

namespace Kombin.Api.Persistence.Entities.Wardrobe;

/// <summary>
/// One garment vision cut out of a <see cref="BulkBatch"/>, waiting for the review screen. <see cref="BaseEntity.Id"/> is the ID the wardrobe item
/// gets at confirm (<c>wardrobe.items.id = bulk_pieces.id</c>), so a retried confirm can never make two items. The analysed columns repeat the
/// item's, with the same types and defaults (<c>BulkPieceMirrorsItemTests</c> pins that every column the single add's analysis sets exists
/// here); the staged images have private names (<c>staged.*</c>, <c>stagedthumb.*</c>) and are copied to the public <c>cutout.*</c> and
/// <c>thumb.*</c> keys only at confirm. Deleted at confirm and cancel; cascades on the batch and on the user.
/// </summary>
internal sealed class BulkPiece : BaseEntity
{
    public Guid BatchId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The reading order the service sent the pieces in.</summary>
    public int SortOrder { get; set; }

    // The piece's box on the photo, fractions (0-1) like /v1/detect: left, top, width, height.
    public double BoxX { get; set; }

    public double BoxY { get; set; }

    public double BoxW { get; set; }

    public double BoxH { get; set; }

    /// <summary>Whether the review screen starts the piece ticked (the service: no reasons). A hint about a duplicate never changes it.</summary>
    public bool PreTicked { get; set; }

    /// <summary>The service's reason codes, as sent; the first one's sentence goes under the thumbnail.</summary>
    public string[] Reasons { get; set; } = [];

    // The analysed item columns, as WardrobeItem's.
    public Guid? CategoryId { get; set; }

    public ProductColor PrimaryColor { get; set; } = ProductColor.Other;

    public ProductColor? SecondaryColor { get; set; }

    public ProductPattern Pattern { get; set; } = ProductPattern.Other;

    public short Formality { get; set; } = 3;

    public Season Seasons { get; set; } = Season.AllYear;

    public string[] Warnings { get; set; } = [];

    public Vector? Embedding { get; set; }

    public string? EmbeddingModel { get; set; }

    public string? AnalysisJson { get; set; }

    /// <summary>The staged cutout (private name, the review image) and thumbnail (private name, the grid); copied to the item's public keys at confirm.</summary>
    public string? CutoutKey { get; set; }

    public string? ThumbKey { get; set; }

    /// <summary>The user's own item this piece looks like (the hint "Dolabında benzeri var."); no foreign key, it is checked again when read.</summary>
    public Guid? SimilarItemId { get; set; }
}
