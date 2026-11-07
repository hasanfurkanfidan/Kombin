using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Recommendation.GetDaily;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Social.TryWithWardrobe;

internal sealed class TryWithWardrobeValidator : AbstractValidator<TryWithWardrobeRequest>
{
    public TryWithWardrobeValidator()
    {
        RuleFor(request => request.Seed).GreaterThanOrEqualTo(0);

        RuleFor(request => request.Occasion)
            .Must(EnumNames.IsValid<Occasion>)
            .WithMessage(DailyOutfitQueryValidator.OccasionMessage)
            .When(request => request.Occasion is not null);
    }
}
