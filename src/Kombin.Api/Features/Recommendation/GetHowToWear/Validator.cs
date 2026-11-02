using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Recommendation.GetDaily;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.GetHowToWear;

internal sealed class HowToWearQueryValidator : AbstractValidator<HowToWearQuery>
{
    public HowToWearQueryValidator()
    {
        RuleFor(query => query)
            .Must(query => (query.ProductId is null) != (query.WardrobeItemId is null))
            .OverridePropertyName("productId")
            .WithMessage("Exactly one of 'productId' and 'wardrobeItemId' is required.");

        RuleFor(query => query.Count).InclusiveBetween(1, GetHowToWearEndpoint.MaxCount);
        RuleFor(query => query.Seed).GreaterThanOrEqualTo(0);

        RuleFor(query => query.Occasion)
            .Must(EnumNames.IsValid<Occasion>)
            .WithMessage(DailyOutfitQueryValidator.OccasionMessage)
            .When(query => query.Occasion is not null);
    }
}
