using System.Linq.Expressions;
using System.Reflection;
using Kombin.Api.Persistence.Entities.Catalog;
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

    /// <summary>The top-level category slugs that are not the accessories catch-all: what <see cref="ForCategorySlug"/> maps to a group with a size.</summary>
    public static readonly IReadOnlyList<string> SizedRoots = ["ust-giyim", "alt-giyim", "dis-giyim", "ayakkabi"];

    private static readonly MethodInfo StartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    /// <summary>
    /// "The product's size group is not Accessories" as SQL: its category is one of <see cref="SizedRoots"/> or below one. The same decision as
    /// <see cref="ForCategorySlug"/> (a test compares them on every seeded category), for queries that must filter before they page.
    /// </summary>
    public static Expression<Func<Product, bool>> HasSizeGroup()
    {
        var product = Expression.Parameter(typeof(Product), "product");
        var slug = Expression.Property(Expression.Property(product, nameof(Product.Category)), nameof(Category.Slug));
        Expression body = Expression.Constant(false);

        foreach (var root in SizedRoots)
        {
            var isRoot = Expression.Equal(slug, Expression.Constant(root));
            var isBelow = Expression.Call(slug, StartsWith, Expression.Constant(root + "/"));
            body = Expression.OrElse(body, Expression.OrElse(isRoot, isBelow));
        }

        return Expression.Lambda<Func<Product, bool>>(body, product);
    }

    /// <summary>How sizes are stored: trimmed and upper-cased, so "m" and " M " are the same size.</summary>
    public static string NormalizeSize(string size) => size.Trim().ToUpperInvariant();
}
