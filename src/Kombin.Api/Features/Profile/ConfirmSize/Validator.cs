using FluentValidation;
using Kombin.Api.Features.Profile.UpsertSize;

namespace Kombin.Api.Features.Profile.ConfirmSize;

internal sealed class ConfirmSizeValidator : AbstractValidator<ConfirmSizeRequest>
{
    public ConfirmSizeValidator()
    {
        RuleFor(request => request.ProductId).NotEmpty();

        RuleFor(request => request.Size)
            .NotEmpty()
            .Must(size => size is null || size.Trim().Length <= UpsertSizeValidator.MaxSizeLength)
            .WithMessage($"'Size' must be {UpsertSizeValidator.MaxSizeLength} characters or fewer.");

        // A missing flag must not silently mean "did not fit".
        RuleFor(request => request.Fitted).NotNull();
    }
}
