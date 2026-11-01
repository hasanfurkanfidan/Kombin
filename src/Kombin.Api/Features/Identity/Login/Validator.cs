using FluentValidation;

namespace Kombin.Api.Features.Identity.Login;

internal sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);

        // Only presence is checked: complexity rules apply when a password is chosen, not when it is presented.
        RuleFor(request => request.Password)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(request => request.DeviceName)
            .MaximumLength(100);
    }
}
