using System.Text.Json.Serialization;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog;

internal sealed record BrandRef(Guid Id, string Name, string Slug);

internal sealed record CategoryRef(Guid Id, string Slug);

/// <summary>The seller of the offer that defines <c>lowestPrice</c>, for the "from X" label next to the price.</summary>
internal sealed record SellerRef(string Name, string Slug);

/// <summary>Prices and <c>CheapestSeller</c> are those of the product's lowest active, in-stock offer. <c>MatchScore</c> (0-100) is present only when the request named a persona.</summary>
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
    int DiscountPercent,
    SellerRef? CheapestSeller,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MatchScore = null);
