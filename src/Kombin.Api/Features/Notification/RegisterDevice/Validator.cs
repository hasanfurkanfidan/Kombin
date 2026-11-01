using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Features.Notification.RegisterDevice;

internal sealed class RegisterDeviceValidator : AbstractValidator<RegisterDeviceRequest>
{
    public RegisterDeviceValidator()
    {
        // The token's shape is checked by the handler, which answers with Notification.InvalidPushToken.
        RuleFor(request => request.ExpoPushToken).NotEmpty().MaximumLength(PushTokens.MaxLength);

        RuleFor(request => request.Platform)
            .Must(platform => EnumNames.IsValid<DevicePlatform>(platform))
            .WithMessage("'Platform' must be one of: Ios, Android.");

        RuleFor(request => request.AppVersion).MaximumLength(40);
        RuleFor(request => request.DeviceName).MaximumLength(100);
    }
}
