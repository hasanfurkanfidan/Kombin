using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.DismissOutfit;

internal sealed class DismissOutfitRequestValidator : AbstractValidator<DismissOutfitRequest>
{
    public DismissOutfitRequestValidator() =>
        RuleFor(request => request.Reason)
            .Must(EnumNames.IsValid<DismissReason>)
            .WithMessage($"'Reason' must be one of: {string.Join(", ", Enum.GetNames<DismissReason>())}.")
            .When(request => request.Reason is not null);
}
