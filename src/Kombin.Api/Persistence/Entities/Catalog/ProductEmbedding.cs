using Pgvector;

namespace Kombin.Api.Persistence.Entities.Catalog;

internal enum ProductEmbeddingStatus
{
    /// <summary><see cref="ProductEmbedding.Embedding"/> holds the vector of <see cref="ProductEmbedding.SourceImageUrl"/>.</summary>
    Ready,

    /// <summary>
    /// The image cannot be used: it is gone (404/410), is not an accepted image or is too large, or the vision service refused it. Not
    /// retried until the product's image URL changes or <c>ProductEmbeddingRules.UnusableRetryAfter</c> has passed.
    /// </summary>
    Unusable,

    /// <summary>A transient failure (the image host or the vision service did not answer); retried at <see cref="ProductEmbedding.NextAttemptAt"/>.</summary>
    Failed,
}

/// <summary>
/// The visual-search vector of one catalog product: the CLIP embedding of its primary image (the first image when it has none), computed by
/// the <c>product-embeddings</c> job through <c>kombin-vision /v1/embed</c>. One row per product. Derived data: never written by an endpoint.
/// </summary>
internal sealed class ProductEmbedding : IAuditable
{
    public const int Dimensions = 512;

    public Guid ProductId { get; set; }

    /// <summary>
    /// Unit length. Set when <see cref="Status"/> is <see cref="ProductEmbeddingStatus.Ready"/>; a <see cref="ProductEmbeddingStatus.Failed"/> refresh
    /// keeps the previous vector (same product, older image) until it succeeds; an <see cref="ProductEmbeddingStatus.Unusable"/> image clears it.
    /// Search reads every row with a vector of the current model.
    /// </summary>
    public Vector? Embedding { get; set; }

    /// <summary>
    /// The vision service's <c>clipModel</c> (name/version) of <see cref="Embedding"/>; for a row without a vector, the model of the last attempt.
    /// Vectors of different models are not comparable.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The <see cref="ProductImage"/> the URL belongs to, when it is one of the product's images (the primary URL usually is).</summary>
    public Guid? SourceImageId { get; set; }

    /// <summary>The image the vector was computed from; a product whose image URL changes is embedded again.</summary>
    public string SourceImageUrl { get; set; } = string.Empty;

    public ProductEmbeddingStatus Status { get; set; }

    /// <summary>Failed attempts since the last success; drives the back-off of <see cref="ProductEmbeddingStatus.Failed"/> rows.</summary>
    public int Attempts { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>When the job last worked on the row, whatever the outcome; an <c>Unusable</c> image is looked at again 30 days after it.</summary>
    public DateTimeOffset CheckedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Product Product { get; set; } = null!;
}
