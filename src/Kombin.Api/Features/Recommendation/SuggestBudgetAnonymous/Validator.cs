using FluentValidation;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Recommendation.GetDaily;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.SuggestBudgetAnonymous;

internal sealed class AnonymousBudgetOutfitsRequestValidator : AbstractValidator<AnonymousBudgetOutfitsRequest>
{
    private static readonly Audience[] Allowed = [Audience.Women, Audience.Men, Audience.Unisex];

    public AnonymousBudgetOutfitsRequestValidator(IClock clock)
    {
        RuleFor(request => request.Budget).NotNull().InclusiveBetween(BudgetOutfits.MinBudget, BudgetOutfits.MaxBudget);

        RuleFor(request => request.Audience)
            .NotEmpty()
            .Must(audience => EnumNames.TryParse<Audience>(audience, out var parsed) && Allowed.Contains(parsed))
            .WithMessage("'Audience' must be one of: Women, Men, Unisex.");

        RuleFor(request => request.Occasion)
            .Must(EnumNames.IsValid<Occasion>)
            .WithMessage(DailyOutfitQueryValidator.OccasionMessage)
            .When(request => request.Occasion is not null);

        RuleFor(request => request.Date)
            .Must(date => OutfitDates.Resolve(date, OutfitSources.Today(clock.UtcNow)) is not null)
            .WithMessage($"'Date' must be today, tomorrow or a yyyy-MM-dd day within the next {OutfitDates.MaxDaysAhead} days.");

        RuleFor(request => request.Seed).GreaterThanOrEqualTo(0);
        RuleFor(request => request.Count).InclusiveBetween(1, BudgetSearch.MaxCount);
    }
}
