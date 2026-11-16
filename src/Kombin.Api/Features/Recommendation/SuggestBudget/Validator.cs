using FluentValidation;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Recommendation.GetDaily;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.SuggestBudget;

internal sealed class BudgetOutfitsRequestValidator : AbstractValidator<BudgetOutfitsRequest>
{
    public BudgetOutfitsRequestValidator(IClock clock)
    {
        RuleFor(request => request.Budget).NotNull().InclusiveBetween(BudgetOutfits.MinBudget, BudgetOutfits.MaxBudget);

        RuleFor(request => request.Occasion)
            .Must(EnumNames.IsValid<Occasion>)
            .WithMessage(DailyOutfitQueryValidator.OccasionMessage)
            .When(request => request.Occasion is not null);

        RuleFor(request => request.Date)
            .Must(date => OutfitDates.Resolve(date, OutfitSources.Today(clock.UtcNow)) is not null)
            .WithMessage(OutfitDates.Message);

        RuleFor(request => request.Seed).GreaterThanOrEqualTo(0);
        RuleFor(request => request.Count).InclusiveBetween(1, BudgetSearch.MaxCount);
    }
}
