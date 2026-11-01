namespace Kombin.Api.Persistence.Entities.Catalog;

/// <summary>A canonical, real-world item. Each seller listing of it is an <see cref="Offer"/>.</summary>
internal sealed class Product : BaseEntity
{
    public Guid BrandId { get; set; }

    public Guid CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Unique within the brand.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The brand's own model/SKU family code, stored trimmed and upper-cased; unique within the brand when present.</summary>
    public string? ModelCode { get; set; }

    public string? Description { get; set; }

    public Audience Audience { get; set; }

    public ProductColor PrimaryColor { get; set; }

    public ProductPattern Pattern { get; set; }

    public ProductFit Fit { get; set; }

    /// <summary>1 = sportswear, 5 = formal.</summary>
    public short Formality { get; set; }

    public Season Seasons { get; set; }

    public string? Material { get; set; }

    /// <summary>False when no active offer remains.</summary>
    public bool IsActive { get; set; } = true;

    public string? PrimaryImageUrl { get; set; }

    public Brand Brand { get; set; } = null!;

    public Category Category { get; set; } = null!;

    public List<ProductImage> Images { get; } = [];

    public List<Offer> Offers { get; } = [];
}
