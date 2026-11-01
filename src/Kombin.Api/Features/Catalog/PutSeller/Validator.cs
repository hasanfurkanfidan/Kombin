using FluentValidation;
using Kombin.Api.Features.Catalog.UpsertOffers;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.PutSeller;

internal sealed class PutSellerRouteValidator : AbstractValidator<PutSellerRoute>
{
    public PutSellerRouteValidator() =>
        RuleFor(route => route.SellerSlug)
            .NotEmpty()
            .MaximumLength(120)
            .Must(slug => Slugs.Slugify(slug) == slug).WithMessage("'Seller Slug' must be lowercase kebab-case.");
}

internal sealed class PutSellerValidator : AbstractValidator<PutSellerRequest>
{
    private const int MaxParams = 20;

    private const int MaxParamKeyLength = 50;

    private const int MaxParamValueLength = 200;

    public PutSellerValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(100);

        RuleFor(request => request.Kind)
            .Must(EnumNames.IsValid<SellerKind>)
            .WithMessage(UpsertOfferItemValidator.OneOf<SellerKind>("Kind"));

        RuleFor(request => request.WebsiteUrl)
            .NotEmpty()
            .MaximumLength(2048)
            .Must(UpsertOfferItemValidator.IsPublicHttpUrl).WithMessage("'Website Url' must be an absolute http or https URL without credentials or spaces.");

        RuleFor(request => request.AffiliateNetwork).MaximumLength(50);

        // Without one of the two placeholders the redirect could not reach the product.
        RuleFor(request => request.AffiliateUrlTemplate!)
            .MaximumLength(1024)
            .Must(template => template.Contains("{url}", StringComparison.Ordinal) || template.Contains("{rawUrl}", StringComparison.Ordinal))
            .WithMessage("'Affiliate Url Template' must contain {url} or {rawUrl}.")
            .When(request => !string.IsNullOrWhiteSpace(request.AffiliateUrlTemplate));

        RuleFor(request => request.AffiliateParams!)
            .Must(parameters => parameters.Count <= MaxParams)
            .WithMessage($"'Affiliate Params' must not contain more than {MaxParams} entries.")
            .Must(parameters => parameters.All(pair => IsPlaceholderName(pair.Key) && pair.Value is { Length: <= MaxParamValueLength }))
            .WithMessage($"'Affiliate Params' keys must start with a letter and use letters, digits, '_', '.' or '-' (at most {MaxParamKeyLength}); values are text of at most {MaxParamValueLength} characters.")
            .When(request => request.AffiliateParams is not null);
    }

    /// <summary>Keys are read in a template as <c>{p.key}</c>, so they must be usable as a placeholder name.</summary>
    private static bool IsPlaceholderName(string key) =>
        key.Length is > 0 and <= MaxParamKeyLength
        && char.IsAsciiLetter(key[0])
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
}
