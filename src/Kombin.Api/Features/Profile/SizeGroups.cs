using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

internal static class SizeGroups
{
    /// <summary>
    /// The size group of a category, by its top-level slug (<c>ust-giyim/tisort</c> and <c>ust-giyim</c> are both Tops). A category
    /// outside the five known roots counts as Accessories, the catch-all.
    /// </summary>
    public static SizeGroup ForCategorySlug(string categorySlug)
    {
        var root = categorySlug.Split('/')[0];

        return root switch
        {
            "ust-giyim" => SizeGroup.Tops,
            "alt-giyim" => SizeGroup.Bottoms,
            "dis-giyim" => SizeGroup.Outerwear,
            "ayakkabi" => SizeGroup.Shoes,
            _ => SizeGroup.Accessories,
        };
    }

    /// <summary>How sizes are stored: trimmed and upper-cased, so "m" and " M " are the same size.</summary>
    public static string NormalizeSize(string size) => size.Trim().ToUpperInvariant();
}
