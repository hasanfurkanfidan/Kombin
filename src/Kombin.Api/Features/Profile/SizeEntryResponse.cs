using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

internal sealed record SizeEntryResponse(
    Guid Id,
    BrandRef Brand,
    SizeGroup Group,
    string Size,
    SizeSource Source,
    decimal Confidence,
    DateTimeOffset UpdatedAt)
{
    public static SizeEntryResponse From(SizeEntry entry, Brand brand) => new(
        entry.Id,
        new BrandRef(brand.Id, brand.Name, brand.Slug),
        entry.Group,
        entry.Size,
        entry.Source,
        entry.Confidence,
        entry.UpdatedAt);
}
