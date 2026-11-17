namespace Kombin.Api.Features.TryOn.Rules;

/// <summary>The renderer's garment families (<c>/v1/garment/prepare</c>'s <c>family</c>).</summary>
internal enum TryOnFamily
{
    Top,
    Outerwear,
    Trousers,
    Skirt,
    Shoes,
    Bag,
    Belt,
}

/// <summary>
/// Which family a category is, and which families are core. Pure. A prepared row's own <c>family</c> (the service's answer) wins over the
/// category; the category decides only for a piece that has not been answered yet. Leaf slugs as the vision service's categories (the blazer is
/// outerwear, as in its detector table); a category without a family (a bare root, a new one) is never prepared.
/// </summary>
internal static class TryOnFamilies
{
    private static readonly Dictionary<string, TryOnFamily> ByCategory = new(StringComparer.Ordinal)
    {
        ["ust-giyim/tisort"] = TryOnFamily.Top,
        ["ust-giyim/gomlek"] = TryOnFamily.Top,
        ["ust-giyim/triko"] = TryOnFamily.Top,
        ["ust-giyim/blazer"] = TryOnFamily.Outerwear,
        ["dis-giyim/mont"] = TryOnFamily.Outerwear,
        ["dis-giyim/kaban"] = TryOnFamily.Outerwear,
        ["dis-giyim/ceket"] = TryOnFamily.Outerwear,
        ["alt-giyim/pantolon"] = TryOnFamily.Trousers,
        ["alt-giyim/jean"] = TryOnFamily.Trousers,
        ["alt-giyim/etek"] = TryOnFamily.Skirt,
        ["ayakkabi/sneaker"] = TryOnFamily.Shoes,
        ["ayakkabi/bot"] = TryOnFamily.Shoes,
        ["ayakkabi/loafer"] = TryOnFamily.Shoes,
        ["aksesuar/canta"] = TryOnFamily.Bag,
        ["aksesuar/kemer"] = TryOnFamily.Belt,
    };

    /// <summary>The categories that are prepared at all (the job's SQL filter).</summary>
    public static IReadOnlyCollection<string> CategorySlugs => ByCategory.Keys;

    public static TryOnFamily? ForCategory(string? categorySlug) =>
        categorySlug is not null && ByCategory.TryGetValue(categorySlug, out var family) ? family : null;

    /// <summary>The service's lower-case name (<c>top</c>, <c>outerwear</c>, ...), or null for anything else.</summary>
    public static TryOnFamily? Parse(string? family) => family switch
    {
        "top" => TryOnFamily.Top,
        "outerwear" => TryOnFamily.Outerwear,
        "trousers" => TryOnFamily.Trousers,
        "skirt" => TryOnFamily.Skirt,
        "shoes" => TryOnFamily.Shoes,
        "bag" => TryOnFamily.Bag,
        "belt" => TryOnFamily.Belt,
        _ => null,
    };

    /// <summary>Top or outerwear, bottom, shoes: the pieces whose readiness decides. Bags and belts never block.</summary>
    public static bool IsCore(TryOnFamily family) => family is not (TryOnFamily.Bag or TryOnFamily.Belt);

    public static bool IsUpper(TryOnFamily family) => family is TryOnFamily.Top or TryOnFamily.Outerwear;

    public static bool IsBottom(TryOnFamily family) => family is TryOnFamily.Trousers or TryOnFamily.Skirt;
}
