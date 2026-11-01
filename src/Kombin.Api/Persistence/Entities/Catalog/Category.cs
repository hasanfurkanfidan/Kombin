namespace Kombin.Api.Persistence.Entities.Catalog;

internal sealed class Category : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Stable API identifier. Children are path-style, e.g. <c>ust-giyim/tisort</c>.</summary>
    public string Slug { get; set; } = string.Empty;

    public Guid? ParentId { get; set; }

    public int SortOrder { get; set; }
}
