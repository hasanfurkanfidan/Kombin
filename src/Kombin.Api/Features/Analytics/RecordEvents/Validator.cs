using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Analytics;

namespace Kombin.Api.Features.Analytics.RecordEvents;

/// <summary>
/// Only the shape of the body fails the request (400). Whether an event is one the API accepts (its name and properties) is decided per
/// event by <see cref="AnalyticsEventCatalog.Reject"/> and costs only that event.
/// </summary>
internal sealed class RecordEventsValidator : AbstractValidator<RecordEventsRequest>
{
    public const int MaxEvents = 50;

    public RecordEventsValidator()
    {
        RuleFor(request => request.Events).NotEmpty();

        RuleFor(request => request.Events)
            .Must(events => events.Count <= MaxEvents)
            .WithMessage($"'Events' must not contain more than {MaxEvents} entries.")
            .When(request => request.Events is not null);

        // An oversized batch is already refused above; do not validate thousands of events first.
        RuleForEach(request => request.Events)
            .NotNull()
            .SetValidator(new EventItemValidator())
            .When(request => request.Events is { Count: <= MaxEvents });
    }
}

internal sealed class EventItemValidator : AbstractValidator<EventItem>
{
    public EventItemValidator()
    {
        RuleFor(item => item.SessionId).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9\\-_.:]+$");

        RuleFor(item => item.Platform)
            .Must(EnumNames.IsValid<AnalyticsPlatform>)
            .WithMessage($"'Platform' must be one of: {string.Join(", ", Enum.GetNames<AnalyticsPlatform>())}.");

        RuleFor(item => item.AppVersion).NotEmpty().MaximumLength(40);
        RuleFor(item => item.OccurredAt).NotNull();
        RuleFor(item => item.AnonymousId).MustBeAnonymousId();
    }
}
