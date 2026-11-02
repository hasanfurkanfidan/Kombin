using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.UpdateItem;

internal sealed class UpdateItemValidator : AbstractValidator<UpdateItemRequest>
{
    public UpdateItemValidator()
    {
        RuleFor(request => request.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("'Name' must not be blank; use 'clear' to remove it.")
            .MaximumLength(WardrobeItem.MaxNameLength)
            .When(request => request.Name is not null);

        RuleFor(request => request.CategorySlug).NotEmpty().MaximumLength(100).When(request => request.CategorySlug is not null);

        RuleFor(request => request.PrimaryColor).Must(EnumNames.IsValid<ProductColor>).When(request => request.PrimaryColor is not null)
            .WithMessage($"'Primary Color' must be one of: {string.Join(", ", Enum.GetNames<ProductColor>())}.");
        RuleFor(request => request.SecondaryColor).Must(EnumNames.IsValid<ProductColor>).When(request => request.SecondaryColor is not null)
            .WithMessage($"'Secondary Color' must be one of: {string.Join(", ", Enum.GetNames<ProductColor>())}.");
        RuleFor(request => request.Pattern).Must(EnumNames.IsValid<ProductPattern>).When(request => request.Pattern is not null)
            .WithMessage($"'Pattern' must be one of: {string.Join(", ", Enum.GetNames<ProductPattern>())}.");
        RuleFor(request => request.Fit).Must(EnumNames.IsValid<ProductFit>).When(request => request.Fit is not null)
            .WithMessage($"'Fit' must be one of: {string.Join(", ", Enum.GetNames<ProductFit>())}.");

        RuleFor(request => request.Formality).InclusiveBetween((short)1, (short)5);

        RuleFor(request => request.Seasons!)
            .NotEmpty()
            .WithMessage("'Seasons' must name at least one season.")
            .When(request => request.Seasons is not null);
        RuleForEach(request => request.Seasons)
            .Must(EnumNames.IsValidSeason)
            .WithMessage("'Seasons' entries must be Spring, Summer, Autumn or Winter.");

        RuleFor(request => request.BrandId).NotEqual(Guid.Empty);

        RuleFor(request => request.Size)
            .Must(size => !string.IsNullOrWhiteSpace(size))
            .WithMessage("'Size' must not be blank; use 'clear' to remove it.")
            .MaximumLength(WardrobeItem.MaxSizeLength)
            .When(request => request.Size is not null);

        RuleFor(request => request.PurchasePrice)
            .GreaterThanOrEqualTo(0m)
            .PrecisionScale(12, 2, ignoreTrailingZeros: true);

        RuleForEach(request => request.Clear)
            .Must(field => UpdateItemRequest.ClearableFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"'Clear' entries must be one of: {string.Join(", ", UpdateItemRequest.ClearableFields)}.");

        RuleFor(request => request)
            .Must(request => !ClearedAndSet(request).Any())
            .OverridePropertyName("clear")
            .WithMessage(request => $"A field cannot be set and cleared at once: {string.Join(", ", ClearedAndSet(request))}.");
    }

    private static IEnumerable<string> ClearedAndSet(UpdateItemRequest request)
    {
        var set = new Dictionary<string, bool>
        {
            ["name"] = request.Name is not null,
            ["secondaryColor"] = request.SecondaryColor is not null,
            ["brandId"] = request.BrandId is not null,
            ["size"] = request.Size is not null,
            ["purchasePrice"] = request.PurchasePrice is not null,
            ["purchasedAt"] = request.PurchasedAt is not null,
        };

        return set.Where(field => field.Value && request.Clears(field.Key)).Select(field => field.Key);
    }
}
