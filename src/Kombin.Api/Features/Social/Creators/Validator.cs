using System.Text.RegularExpressions;
using FluentValidation;
using Kombin.Api.Features.Catalog.UpsertOffers;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.Creators;

internal sealed partial class PutCreatorValidator : AbstractValidator<PutCreatorRequest>
{
    public PutCreatorValidator()
    {
        RuleFor(request => request.Handle)
            .NotEmpty()
            .Must(handle => HandlePattern().IsMatch(handle?.Trim().TrimStart('@').ToLowerInvariant() ?? string.Empty))
            .WithMessage($"'Handle' must be 3-{CreatorProfile.MaxHandleLength} characters of a-z, 0-9, '.' and '_'.");

        RuleFor(request => request.Bio).MaximumLength(CreatorProfile.MaxBioLength);

        RuleFor(request => request.Link)
            .MaximumLength(CreatorProfile.MaxLinkLength)
            .Must(UpsertOfferItemValidator.IsPublicHttpUrl)
            .When(request => !string.IsNullOrWhiteSpace(request.Link))
            .WithMessage("'Link' must be an absolute http or https URL without credentials or spaces.");
    }

    [GeneratedRegex("^[a-z0-9._]{3,30}$")]
    private static partial Regex HandlePattern();
}
