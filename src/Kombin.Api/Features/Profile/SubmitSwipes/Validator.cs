using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile.SubmitSwipes;

internal sealed class SubmitSwipesValidator : AbstractValidator<SubmitSwipesRequest>
{
    public SubmitSwipesValidator()
    {
        RuleFor(request => request.Swipes)
            .NotNull()
            .Must(swipes => swipes.Count is >= 1 and <= SubmitSwipesEndpoint.MaxBatchSize)
            .WithMessage($"'Swipes' must contain between 1 and {SubmitSwipesEndpoint.MaxBatchSize} items.")
            .When(request => request.Swipes is not null);

        RuleForEach(request => request.Swipes).ChildRules(swipe =>
        {
            swipe.RuleFor(item => item.StyleCardId).NotEmpty();
            swipe.RuleFor(item => item.Decision)
                .Must(EnumNames.IsValid<SwipeDecision>)
                .WithMessage($"'Decision' must be one of: {string.Join(", ", Enum.GetNames<SwipeDecision>())}.");
        });
    }
}
