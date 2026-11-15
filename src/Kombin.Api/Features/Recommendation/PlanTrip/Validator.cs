using FluentValidation;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Recommendation.GetDaily;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.PlanTrip;

internal sealed class TripPlanRequestValidator : AbstractValidator<TripPlanRequest>
{
    public TripPlanRequestValidator(IClock clock)
    {
        RuleFor(request => request.City).NotEmpty().MaximumLength(Trip.MaxCityLength).Must(city => !string.IsNullOrWhiteSpace(city)).WithMessage("'City' must not be blank.");

        RuleFor(request => request.StartDate)
            .NotNull()
            .Must(start => start >= OutfitSources.Today(clock.UtcNow) && start <= OutfitSources.Today(clock.UtcNow).AddDays(TripPlanning.MaxDaysAhead))
            .WithMessage($"'Start Date' must be between today and {TripPlanning.MaxDaysAhead} days ahead.");

        RuleFor(request => request.EndDate)
            .NotNull()
            .Must((request, end) => request.StartDate is null || (end >= request.StartDate && end!.Value.DayNumber - request.StartDate.Value.DayNumber < TripPlanning.MaxDays))
            .WithMessage($"'End Date' must be on or after the start date, for a trip of at most {TripPlanning.MaxDays} days.");

        RuleFor(request => request.Occasions)
            .Must(occasions => occasions!.Count <= TripPlanning.MaxDays)
            .WithMessage($"At most {TripPlanning.MaxDays} plans.")
            .Must(occasions => occasions!.Select(entry => entry?.Date).Distinct().Count() == occasions!.Count)
            .WithMessage("One plan per day.")
            .When(request => request.Occasions is not null);

        RuleForEach(request => request.Occasions).ChildRules(entry =>
        {
            entry.RuleFor(occasion => occasion.Date).NotNull();
            entry.RuleFor(occasion => occasion.Occasion).NotEmpty().Must(EnumNames.IsValid<Occasion>).WithMessage(DailyOutfitQueryValidator.OccasionMessage);
        });

        RuleForEach(request => request.Occasions)
            .Must((request, entry) => entry?.Date is null || request.StartDate is null || request.EndDate is null
                || (entry.Date >= request.StartDate && entry.Date <= request.EndDate))
            .WithMessage("A plan's date must be within the trip.");

        RuleFor(request => request.MaxPieces).InclusiveBetween(TripPlanning.MinPieces, TripPlanning.MaxPieces);
    }
}
