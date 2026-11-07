using FluentValidation;
using Kombin.Api.Features.Catalog;

namespace Kombin.Api.Features.Social.Review;

internal sealed class ReviewPostValidator : AbstractValidator<ReviewPostRequest>
{
    public ReviewPostValidator()
    {
        RuleFor(request => request.Decision)
            .NotEmpty()
            .Must(EnumNames.IsValid<ReviewDecision>)
            .WithMessage("'Decision' must be 'Approve' or 'Remove'.");
    }
}
