using FluentValidation;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Identity.ExternalSignIn;
using Kombin.Api.Persistence.Entities.Identity;

namespace Kombin.Api.Features.Identity.CancelDeletion;

internal sealed class CancelDeletionValidator : AbstractValidator<CancelDeletionRequest>
{
    public CancelDeletionValidator()
    {
        // Either an ID token (with its provider) or an email and password: the two ways of signing in that exist.
        When(request => !string.IsNullOrEmpty(request.IdToken), () =>
        {
            RuleFor(request => request.Provider)
                .NotEmpty()
                .Must(provider => provider is not null && ExternalProviders.All.Contains(provider.Trim().ToLowerInvariant()))
                .WithMessage("'Provider' must be 'google' or 'apple'.");

            RuleFor(request => request.IdToken)
                .MaximumLength(ExternalSignInValidator.MaxIdTokenLength);

            RuleFor(request => request.Nonce)
                .MaximumLength(512);
        }).Otherwise(() =>
        {
            RuleFor(request => request.Email)
                .NotEmpty()
                .EmailAddress()
                .MaximumLength(320);

            RuleFor(request => request.Password)
                .NotEmpty()
                .MaximumLength(128);
        });

        RuleFor(request => request.DeviceName)
            .MaximumLength(100);

        RuleFor(request => request.AnonymousId).MustBeAnonymousId();
    }
}
