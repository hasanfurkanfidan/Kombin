using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Persistence.Configurations.Catalog;

/// <summary>Reference data inserted by the <c>Catalog</c> migration. IDs and timestamps are fixed so the migration is deterministic.</summary>
internal static class CatalogSeed
{
    public static readonly DateTimeOffset SeededAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public static Guid SellerId(int number) => new($"5e110000-0000-7000-8000-{number:D12}");

    public static Guid CategoryId(int number) => new($"ca7e0000-0000-7000-8000-{number:D12}");

    public static IReadOnlyList<Category> Categories()
    {
        (string Slug, string Name, (string Slug, string Name)[] Children)[] tree =
        [
            ("ust-giyim", "Üst Giyim", [("ust-giyim/tisort", "Tişört"), ("ust-giyim/gomlek", "Gömlek"), ("ust-giyim/triko", "Triko"), ("ust-giyim/blazer", "Blazer")]),
            ("alt-giyim", "Alt Giyim", [("alt-giyim/pantolon", "Pantolon"), ("alt-giyim/jean", "Jean"), ("alt-giyim/etek", "Etek")]),
            ("dis-giyim", "Dış Giyim", [("dis-giyim/mont", "Mont"), ("dis-giyim/kaban", "Kaban"), ("dis-giyim/ceket", "Ceket")]),
            ("ayakkabi", "Ayakkabı", [("ayakkabi/sneaker", "Sneaker"), ("ayakkabi/bot", "Bot"), ("ayakkabi/loafer", "Loafer")]),
            ("aksesuar", "Aksesuar", [("aksesuar/canta", "Çanta"), ("aksesuar/kemer", "Kemer")]),
        ];

        var categories = new List<Category>();
        var number = 0;

        for (var parentIndex = 0; parentIndex < tree.Length; parentIndex++)
        {
            var (slug, name, children) = tree[parentIndex];
            var parent = Create(++number, slug, name, parentId: null, sortOrder: (parentIndex + 1) * 10);
            categories.Add(parent);

            for (var childIndex = 0; childIndex < children.Length; childIndex++)
            {
                categories.Add(Create(++number, children[childIndex].Slug, children[childIndex].Name, parent.Id, sortOrder: (childIndex + 1) * 10));
            }
        }

        return categories;
    }

    private static Category Create(int number, string slug, string name, Guid? parentId, int sortOrder) => new()
    {
        Id = CategoryId(number),
        Name = name,
        Slug = slug,
        ParentId = parentId,
        SortOrder = sortOrder,
        CreatedAt = SeededAt,
        UpdatedAt = SeededAt,
    };
}
