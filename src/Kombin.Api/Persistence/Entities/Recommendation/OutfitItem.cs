namespace Kombin.Api.Persistence.Entities.Recommendation;

/// <summary>One piece of an outfit: a wardrobe item, or a catalog product with the offer that was cheapest when it was suggested.</summary>
internal sealed class OutfitItem
{
    public long Id { get; set; }

    public Guid OutfitId { get; set; }

    public OutfitSlot Slot { get; set; }

    public OutfitItemRole Role { get; set; }

    public Guid? WardrobeItemId { get; set; }

    public Guid? ProductId { get; set; }

    public Guid? OfferId { get; set; }

    /// <summary>The offer's effective price (price plus shipping) at suggestion time; catalog pieces only.</summary>
    public decimal? Price { get; set; }

    public int Position { get; set; }
}
