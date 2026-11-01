using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Analytics;

namespace Kombin.Api.Features.Analytics.RecordEvents;

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
        // The error code is what the API reports (Analytics.UnknownEvent) when any event of the batch has an unknown name.
        RuleFor(item => item.Name)
            .Must(AnalyticsEventCatalog.IsKnown)
            .WithErrorCode(AnalyticsErrors.UnknownEvent.Code)
            .WithMessage(item => $"'{Truncate(item.Name)}' is not an event name the API accepts.");

        RuleFor(item => item.SessionId).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9\\-_.:]+$");

        RuleFor(item => item.Platform)
            .Must(EnumNames.IsValid<AnalyticsPlatform>)
            .WithMessage($"'Platform' must be one of: {string.Join(", ", Enum.GetNames<AnalyticsPlatform>())}.");

        RuleFor(item => item.AppVersion).NotEmpty().MaximumLength(40);
        RuleFor(item => item.OccurredAt).NotNull();
        RuleFor(item => item.AnonymousId).MustBeAnonymousId();

        RuleFor(item => item.Properties)
            .Must(properties => AnalyticsEventCatalog.SerializedSize(properties) <= AnalyticsEventCatalog.MaxPropertiesBytes)
            .WithMessage($"'Properties' must not be larger than {AnalyticsEventCatalog.MaxPropertiesBytes} bytes.");

        RuleFor(item => item.Properties).Custom((properties, context) =>
        {
            foreach (var (key, message) in AnalyticsEventCatalog.Check(context.InstanceToValidate.Name, properties))
            {
                context.AddFailure($"Properties.{key}", message);
            }
        });
    }

    // The name is client input echoed in the error; keep a hostile one from bloating the response.
    private static string Truncate(string? name) =>
        name is null ? string.Empty : name.Length <= AnalyticsEventCatalog.MaxNameLength ? name : name[..AnalyticsEventCatalog.MaxNameLength];
}
