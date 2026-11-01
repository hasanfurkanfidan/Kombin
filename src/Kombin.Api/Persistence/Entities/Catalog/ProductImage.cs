namespace Kombin.Api.Persistence.Entities.Catalog;

internal sealed class ProductImage : BaseEntity
{
    public Guid ProductId { get; set; }

    public string Url { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
