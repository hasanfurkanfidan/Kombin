using FluentValidation;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Recommendation.PlanTrip;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.SaveTrip;

internal sealed class SaveTripRequestValidator : AbstractValidator<SaveTripRequest>
{
    public SaveTripRequestValidator(IClock clock)
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(Trip.MaxNameLength).Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("'Name' must not be blank.");
        RuleFor(request => request.Request).NotNull().SetValidator(new TripPlanRequestValidator(clock)!);
    }
}
