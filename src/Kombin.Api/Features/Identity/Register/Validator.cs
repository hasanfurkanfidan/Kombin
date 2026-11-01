using FluentValidation;
using Kombin.Api.Features.Analytics;

namespace Kombin.Api.Features.Identity.Register;

internal sealed class RegisterValidator : AbstractValidator<RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);

        RuleFor(request => request.Password)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128)
            .Must(password => password.Any(char.IsLetter)).WithMessage("'Password' must contain at least one letter.")
            .Must(password => password.Any(char.IsDigit)).WithMessage("'Password' must contain at least one digit.");

        RuleFor(request => request.DisplayName)
            .NotEmpty()
            .Must(displayName => displayName.Trim().Length is >= 2 and <= 50)
            .WithMessage("'Display Name' must be between 2 and 50 characters.");

        RuleFor(request => request.AnonymousId).MustBeAnonymousId();
    }
}
