using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.RecordFeedback;

internal sealed class OutfitFeedbackRequestValidator : AbstractValidator<OutfitFeedbackRequest>
{
    public OutfitFeedbackRequestValidator()
    {
        RuleFor(request => request.Action)
            .Must(EnumNames.IsValid<OutfitFeedbackAction>)
            .WithMessage($"'Action' must be one of: {string.Join(", ", Enum.GetNames<OutfitFeedbackAction>())}.");

        RuleFor(request => request.Reason)
            .Must(EnumNames.IsValid<DismissReason>)
            .WithMessage($"'Reason' must be one of: {string.Join(", ", Enum.GetNames<DismissReason>())}.")
            .When(request => request.Reason is not null);

        RuleFor(request => request.Reason)
            .Null()
            .WithMessage("'Reason' is only accepted with the Dismiss action.")
            .When(request => EnumNames.TryParse<OutfitFeedbackAction>(request.Action, out var action) && action != OutfitFeedbackAction.Dismiss);
    }
}
