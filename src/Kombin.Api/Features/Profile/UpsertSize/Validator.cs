using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile.UpsertSize;

internal sealed class UpsertSizeValidator : AbstractValidator<UpsertSizeRequest>
{
    public const int MaxSizeLength = 20;

    public UpsertSizeValidator()
    {
        RuleFor(request => request.BrandSlug).NotEmpty().MaximumLength(100);

        RuleFor(request => request.Group)
            .Must(EnumNames.IsValid<SizeGroup>)
            .WithMessage($"'Group' must be one of: {string.Join(", ", Enum.GetNames<SizeGroup>())}.");

        RuleFor(request => request.Size)
            .NotEmpty()
            .Must(size => size is null || size.Trim().Length <= MaxSizeLength)
            .WithMessage($"'Size' must be {MaxSizeLength} characters or fewer.");
    }
}
