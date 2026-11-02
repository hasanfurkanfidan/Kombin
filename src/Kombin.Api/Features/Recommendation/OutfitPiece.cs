using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

internal enum PieceSource
{
    Wardrobe,
    Catalog,
}

/// <summary>
/// A garment as the engine sees it, whether the user owns it or not. <c>Id</c> is the wardrobe item ID or the product ID.
/// <c>PersonaScore</c> is the persona's <see cref="MatchScorer"/> score (0-100; 50 without a persona). Catalog pieces carry the offer
/// that is cheapest by effective price.
/// </summary>
internal sealed record OutfitPiece(
    PieceSource Source,
    Guid Id,
    OutfitSlot Slot,
    string CategorySlug,
    ProductColor Color,
    ProductPattern Pattern,
    ProductFit Fit,
    int Formality,
    Season Seasons,
    int PersonaScore = 50,
    DateOnly? LastWornAt = null,
    Guid? OfferId = null,
    decimal? Price = null)
{
    /// <summary>The leaf of the category slug: <c>ust-giyim/gomlek</c> → <c>gomlek</c>.</summary>
    public string Leaf => CategorySlug[(CategorySlug.LastIndexOf('/') + 1)..];

    /// <summary>Unique across sources: <c>w:{id}</c> or <c>p:{id}</c>.</summary>
    public string Key => $"{(Source == PieceSource.Wardrobe ? "w" : "p")}:{Id:N}";

    public ProductTraits Traits => new(Color, Formality, Fit, Seasons, Pattern, CategorySlug);
}

/// <summary>Which slot a garment fills, by category. The rule a stylist would argue with is the blazer one.</summary>
internal static class OutfitSlots
{
    /// <summary>
    /// Leaves that are not worn in the slot of their category root. A blazer is catalogued as a top, but it is worn over a top
    /// (a shirt or a tee), so in an outfit it is the layer, like a jacket.
    /// </summary>
    private static readonly Dictionary<string, OutfitSlot> LeafOverrides = new(StringComparer.Ordinal)
    {
        ["ust-giyim/blazer"] = OutfitSlot.Outerwear,
    };

    public static OutfitSlot ForCategorySlug(string categorySlug)
    {
        if (LeafOverrides.TryGetValue(categorySlug, out var slot))
        {
            return slot;
        }

        return categorySlug.Split('/')[0] switch
        {
            "ust-giyim" => OutfitSlot.Top,
            "alt-giyim" => OutfitSlot.Bottom,
            "dis-giyim" => OutfitSlot.Outerwear,
            "ayakkabi" => OutfitSlot.Shoes,
            _ => OutfitSlot.Accessory,
        };
    }

    /// <summary>The category slugs whose products fill <paramref name="slot"/>: its roots and leaves, the overrides included.</summary>
    public static bool Fills(string categorySlug, OutfitSlot slot) => ForCategorySlug(categorySlug) == slot;

    /// <summary>The slots every outfit has; outerwear and an accessory are added when they help.</summary>
    public static readonly OutfitSlot[] Core = [OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Shoes];
}
