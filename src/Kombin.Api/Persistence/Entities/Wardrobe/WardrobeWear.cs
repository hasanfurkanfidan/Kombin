namespace Kombin.Api.Persistence.Entities.Wardrobe;

/// <summary>One day a wardrobe item was worn; at most one row per item and day. Not a <see cref="BaseEntity"/>: a bigint log row.</summary>
internal sealed class WardrobeWear
{
    public long Id { get; set; }

    public Guid ItemId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The calendar day in Europe/Istanbul.</summary>
    public DateOnly WornOn { get; set; }

    /// <summary>The outfit it was worn in; outfits arrive with the Recommendation task, so there is no foreign key yet.</summary>
    public Guid? OutfitId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
