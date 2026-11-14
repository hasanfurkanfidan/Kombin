using FluentValidation;

namespace Kombin.Api.Features.Identity.DeleteAccount;

internal sealed class DeleteAccountValidator : AbstractValidator<DeleteAccountRequest>
{
    public DeleteAccountValidator()
    {
        RuleFor(request => request.Password)
            .MaximumLength(128);

        RuleFor(request => request.Confirm)
            .Must(AccountDeletionRules.ConfirmationMatches)
            .WithMessage($"'Confirm' must be \"{AccountDeletionRules.ConfirmWord}\".");
    }
}
