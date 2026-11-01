using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;

namespace Kombin.Api.Features.Engagement.CreateAlert;

internal sealed class CreateAlertValidator : AbstractValidator<CreateAlertRequest>
{
    private const int MaxSizes = 30;

    public CreateAlertValidator()
    {
        RuleFor(request => request.ProductId).NotEmpty();

        RuleFor(request => request.Kind)
            .Must(EnumNames.IsValid<AlertKind>)
            .WithMessage($"'Kind' must be one of: {string.Join(", ", Enum.GetNames<AlertKind>())}.");

        // The target is the whole point of a PriceDrop alert and meaningless for the others.
        RuleFor(request => request.TargetPrice)
            .NotNull().WithMessage("'Target Price' is required for PriceDrop alerts.")
            .When(request => IsKind(request, AlertKind.PriceDrop));
        RuleFor(request => request.TargetPrice)
            .Null().WithMessage("'Target Price' is only allowed for PriceDrop alerts.")
            .When(request => EnumNames.IsValid<AlertKind>(request.Kind) && !IsKind(request, AlertKind.PriceDrop));
        RuleFor(request => request.TargetPrice)
            .GreaterThan(0m)
            .PrecisionScale(12, 2, ignoreTrailingZeros: true);

        RuleFor(request => request.Sizes!)
            .Must(sizes => sizes.Count <= MaxSizes)
            .WithMessage($"'Sizes' must not contain more than {MaxSizes} entries.")
            .When(request => request.Sizes is not null);
        RuleForEach(request => request.Sizes).NotEmpty().MaximumLength(20);
        RuleFor(request => request.Sizes!)
            .Must(sizes => sizes.Count == 0)
            .WithMessage("'Sizes' is only allowed for BackInStock alerts.")
            .When(request => request.Sizes is not null && EnumNames.IsValid<AlertKind>(request.Kind) && !IsKind(request, AlertKind.BackInStock));
    }

    private static bool IsKind(CreateAlertRequest request, AlertKind kind) =>
        EnumNames.TryParse<AlertKind>(request.Kind, out var parsed) && parsed == kind;
}
