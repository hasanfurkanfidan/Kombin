using Kombin.Api.Persistence.Entities.Catalog;
using Pgvector;

namespace Kombin.Api.Persistence.Entities.Wardrobe;

internal enum WardrobeItemStatus
{
    /// <summary>Waiting for the photo or for the <c>wardrobe-analyze</c> job.</summary>
    Processing,
    Ready,

    /// <summary>The analysis gave up; <see cref="WardrobeItem.FailureReason"/> says why. The user can still fill the item in by hand.</summary>
    Failed,
}

/// <summary>
/// A garment the user owns. Created from a photo (analysed by <c>kombin-vision</c>) or from a catalog product. The attributes use the
/// catalog's vocabulary (<see cref="ProductColor"/>, <see cref="ProductPattern"/>, ...) so wardrobe and catalog can be compared.
/// </summary>
internal sealed class WardrobeItem : BaseEntity
{
    public const int MaxNameLength = 100;

    public const int MaxSizeLength = 20;

    public const int EmbeddingDimensions = 512;

    public Guid UserId { get; set; }

    /// <summary>Optional; the app shows category and color when it is empty.</summary>
    public string? Name { get; set; }

    /// <summary>Null while processing, and when the analysis was not sure enough (the app then asks for it).</summary>
    public Guid? CategoryId { get; set; }

    public ProductColor PrimaryColor { get; set; } = ProductColor.Other;

    public ProductColor? SecondaryColor { get; set; }

    public ProductPattern Pattern { get; set; } = ProductPattern.Other;

    public ProductFit Fit { get; set; } = ProductFit.Unknown;

    /// <summary>1 = sportswear, 5 = formal, like <see cref="Product.Formality"/>.</summary>
    public short Formality { get; set; } = 3;

    public Season Seasons { get; set; } = Season.AllYear;

    public Guid? BrandId { get; set; }

    /// <summary>The catalog product this item was added from ("dolabıma ekle").</summary>
    public Guid? ProductId { get; set; }

    public bool FromCatalog { get; set; }

    public string? Size { get; set; }

    public decimal? PurchasePrice { get; set; }

    public DateOnly? PurchasedAt { get; set; }

    public WardrobeItemStatus Status { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>Quality flags from the analysis (<c>not_garment</c>, <c>person_wearing</c>, <c>blurry</c>, ...); they never block the item.</summary>
    public string[] Warnings { get; set; } = [];

    /// <summary>Private: served through a presigned GET only.</summary>
    public string? ImageOriginalKey { get; set; }

    public string? ImageCutoutKey { get; set; }

    public string? ImageThumbKey { get; set; }

    /// <summary>CLIP embedding of the cutout (512 floats, unit length), for visual search later.</summary>
    public Vector? Embedding { get; set; }

    /// <summary>The vision model the embedding came from; embeddings of different models are not comparable.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>The vision response without the images and the embedding, for debugging and re-tagging.</summary>
    public string? AnalysisJson { get; set; }

    /// <summary>Failed calls to the vision service for the current analysis.</summary>
    public int AnalysisAttempts { get; set; }

    /// <summary>When the analysis job was last enqueued; a second <c>process</c> call while it is recent does not enqueue another.</summary>
    public DateTimeOffset? ProcessRequestedAt { get; set; }

    /// <summary>The first edit after the item became ready: the user's one-tap confirmation.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>Maintained from <see cref="WardrobeWear"/> rows: their count.</summary>
    public int WearCount { get; set; }

    /// <summary>Maintained from <see cref="WardrobeWear"/> rows: the latest day.</summary>
    public DateOnly? LastWornAt { get; set; }

    public bool IsArchived { get; set; }

    /// <summary>The <c>wardrobe-cleanup</c> job deletes the stored images 30 days after this.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public int SortOrder { get; set; }
}
