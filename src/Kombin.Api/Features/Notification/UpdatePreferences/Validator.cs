using FluentValidation;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification.UpdatePreferences;

internal sealed class UpdatePreferencesValidator : AbstractValidator<UpdatePreferencesRequest>
{
    public UpdatePreferencesValidator()
    {
        RuleFor(request => request.MaxRecommendationsPerWeek).InclusiveBetween(0, NotificationPreference.MaxRecommendationsPerWeekLimit);

        // A lone start or end is not a format problem: the handler answers it with Notification.QuietHoursIncomplete.
        RuleFor(request => request.QuietHoursStart)
            .Must(value => UpdatePreferencesEndpoint.TryParseTime(value, out _))
            .WithMessage("'Quiet Hours Start' must be a time of day like 22:00.")
            .When(request => request.QuietHoursStart is not null);
        RuleFor(request => request.QuietHoursEnd)
            .Must(value => UpdatePreferencesEndpoint.TryParseTime(value, out _))
            .WithMessage("'Quiet Hours End' must be a time of day like 08:00.")
            .When(request => request.QuietHoursEnd is not null);

        RuleFor(request => request)
            .Must(request => request.QuietHoursStart != request.QuietHoursEnd)
            .OverridePropertyName("quietHoursEnd")
            .WithMessage("'Quiet Hours End' must differ from 'Quiet Hours Start'.")
            .When(request => request.QuietHoursStart is not null && request.QuietHoursEnd is not null);

        RuleFor(request => request)
            .Must(request => request.QuietHoursStart is null && request.QuietHoursEnd is null)
            .OverridePropertyName("clearQuietHours")
            .WithMessage("'Clear Quiet Hours' cannot be combined with quiet hours.")
            .When(request => request.ClearQuietHours == true);
    }
}
