using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Persistence.Entities.Social;

/// <summary>
/// A garment in a post: a box (fractions of the image), what it is, and whether the author confirmed it. Proposals (detected regions, an
/// outfit's pieces) have <see cref="ConfirmedAt"/> null and are never shown to anyone but the author; publishing replaces them with the
/// tags the author confirmed.
/// </summary>
internal sealed class PostTag
{
    public const int MaxLabelLength = 60;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid PostId { get; set; }

    public int Position { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }

    public PostTagKind Kind { get; set; }

    public PostTagSource Source { get; set; }

    /// <summary>The author's wardrobe item (kind <c>Wardrobe</c>).</summary>
    public Guid? WardrobeItemId { get; set; }

    /// <summary>The catalog product (kind <c>Product</c>).</summary>
    public Guid? ProductId { get; set; }

    /// <summary>The cheapest offer when the tag was confirmed, and its effective price (viewers see the current cheapest one).</summary>
    public Guid? OfferId { get; set; }

    public decimal? PriceAtTag { get; set; }

    /// <summary>What the author called it (an <c>Unknown</c> tag's only content).</summary>
    public string? Label { get; set; }

    /// <summary>The detected or chosen category (a proposal's guess, or the wardrobe item's / product's category).</summary>
    public Guid? CategoryId { get; set; }

    public ProductColor? Color { get; set; }

    /// <summary>The detector's confidence for a proposal.</summary>
    public double? Confidence { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
