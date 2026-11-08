using FluentValidation;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Persistence.Entities.Identity;

namespace Kombin.Api.Features.Identity.ExternalSignIn;

internal sealed class ExternalSignInValidator : AbstractValidator<ExternalSignInRequest>
{
    // Real ID tokens are around 1-2 KB; anything far beyond that is not one.
    internal const int MaxIdTokenLength = 8192;

    public ExternalSignInValidator()
    {
        RuleFor(request => request.Provider)
            .NotEmpty()
            .Must(provider => ExternalProviders.All.Contains(provider.Trim().ToLowerInvariant()))
            .WithMessage("'Provider' must be 'google' or 'apple'.");

        RuleFor(request => request.IdToken)
            .NotEmpty()
            .MaximumLength(MaxIdTokenLength);

        RuleFor(request => request.DisplayName)
            .Must(displayName => displayName is null || displayName.Trim().Length <= ExternalSignInRules.DisplayNameMaxLength)
            .WithMessage($"'Display Name' must be {ExternalSignInRules.DisplayNameMaxLength} characters or fewer.");

        RuleFor(request => request.DeviceName)
            .MaximumLength(100);

        RuleFor(request => request.Nonce)
            .MaximumLength(512);

        RuleFor(request => request.AnonymousId).MustBeAnonymousId();
    }
}
