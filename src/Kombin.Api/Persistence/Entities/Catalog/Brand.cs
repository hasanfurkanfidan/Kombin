namespace Kombin.Api.Persistence.Entities.Catalog;

internal sealed class Brand : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Lowercase kebab-case identifier, unique across brands.</summary>
    public string Slug { get; set; } = string.Empty;

    public string? LogoUrl { get; set; }

    public string? WebsiteUrl { get; set; }
}
