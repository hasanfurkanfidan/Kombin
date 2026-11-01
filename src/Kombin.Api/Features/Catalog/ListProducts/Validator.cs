using FluentValidation;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.ListProducts;

internal sealed class ListProductsValidator : AbstractValidator<ListProductsQuery>
{
    public ListProductsValidator()
    {
        // Enum filters arrive as text so that "priceAsc", "PriceAsc" and "priceasc" all work; ASP.NET's own enum binding is case-sensitive.
        RuleFor(query => query.Sort)
            .Must(EnumNames.IsValid<ProductSort>)
            .WithMessage($"'Sort' must be one of: {string.Join(", ", Enum.GetNames<ProductSort>().Select(ToCamelCase))}.")
            .When(query => query.Sort is not null);

        RuleFor(query => query.Color)
            .Must(EnumNames.IsValid<ProductColor>)
            .WithMessage($"'Color' must be one of: {string.Join(", ", Enum.GetNames<ProductColor>())}.")
            .When(query => query.Color is not null);

        RuleFor(query => query.Audience)
            .Must(EnumNames.IsValid<Audience>)
            .WithMessage($"'Audience' must be one of: {string.Join(", ", Enum.GetNames<Audience>())}.")
            .When(query => query.Audience is not null);
    }

    private static string ToCamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
