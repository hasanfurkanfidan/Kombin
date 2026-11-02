using FluentValidation;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.GetDaily;

internal sealed class DailyOutfitQueryValidator : AbstractValidator<DailyOutfitQuery>
{
    public DailyOutfitQueryValidator(IClock clock)
    {
        RuleFor(query => query.Date)
            .Must(date => OutfitDates.Resolve(date, OutfitSources.Today(clock.UtcNow)) is not null)
            .WithMessage($"'Date' must be today, tomorrow or a yyyy-MM-dd day within the next {OutfitDates.MaxDaysAhead} days.");

        RuleFor(query => query.Occasion)
            .Must(EnumNames.IsValid<Occasion>)
            .WithMessage(OccasionMessage)
            .When(query => query.Occasion is not null);

        RuleFor(query => query.Seed).GreaterThanOrEqualTo(0);
    }

    public static string OccasionMessage => $"'Occasion' must be one of: {string.Join(", ", Enum.GetNames<Occasion>().Select(name => name.ToLowerInvariant()))}.";
}
