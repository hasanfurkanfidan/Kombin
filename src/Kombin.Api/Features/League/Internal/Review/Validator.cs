using FluentValidation;

namespace Kombin.Api.Features.League.Internal.Review;

internal sealed class ReviewEntryRequestValidator : AbstractValidator<ReviewEntryRequest>
{
    public ReviewEntryRequestValidator() =>
        RuleFor(request => request.Decision)
            .NotEmpty()
            .Must(decision => LeagueChoices.TryParse<LeagueReviewDecision>(decision, out _))
            .WithMessage("'Decision' must be Restore or Remove.");
}
