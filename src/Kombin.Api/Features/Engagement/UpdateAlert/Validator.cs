using FluentValidation;

namespace Kombin.Api.Features.Engagement.UpdateAlert;

internal sealed class UpdateAlertValidator : AbstractValidator<UpdateAlertRequest>
{
    private const int MaxSizes = 30;

    public UpdateAlertValidator()
    {
        RuleFor(request => request)
            .Must(request => request.TargetPrice is not null || request.Sizes is not null || request.IsActive is not null)
            .OverridePropertyName("body")
            .WithMessage("At least one of 'targetPrice', 'sizes' or 'isActive' must be given.");

        RuleFor(request => request.TargetPrice)
            .GreaterThan(0m)
            .PrecisionScale(12, 2, ignoreTrailingZeros: true);

        RuleFor(request => request.Sizes!)
            .Must(sizes => sizes.Count <= MaxSizes)
            .WithMessage($"'Sizes' must not contain more than {MaxSizes} entries.")
            .When(request => request.Sizes is not null);
        RuleForEach(request => request.Sizes).NotEmpty().MaximumLength(20);
    }
}
