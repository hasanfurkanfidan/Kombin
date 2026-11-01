using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog;

internal sealed record BrandRef(Guid Id, string Name, string Slug);

internal sealed record CategoryRef(Guid Id, string Slug);

/// <summary>Prices are those of the product's lowest active, in-stock offer.</summary>
internal sealed record ProductListItem(
    Guid Id,
    string Name,
    string Slug,
    BrandRef Brand,
    CategoryRef Category,
    string? PrimaryImageUrl,
    ProductColor PrimaryColor,
    decimal LowestPrice,
    decimal? LowestOriginalPrice,
    int OfferCount,
    int DiscountPercent);
