using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Wardrobe.ListItems;

internal sealed class ListItemsValidator : AbstractValidator<ListItemsQuery>
{
    public ListItemsValidator()
    {
        RuleFor(query => query.Color)
            .Must(EnumNames.IsValid<ProductColor>)
            .WithMessage($"'Color' must be one of: {string.Join(", ", Enum.GetNames<ProductColor>())}.")
            .When(query => query.Color is not null);

        RuleFor(query => query.Category).MaximumLength(100);
    }
}
