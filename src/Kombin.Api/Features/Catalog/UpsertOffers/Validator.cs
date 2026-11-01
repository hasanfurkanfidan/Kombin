using FluentValidation;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.UpsertOffers;

internal sealed class UpsertOffersValidator : AbstractValidator<UpsertOffersRequest>
{
    public UpsertOffersValidator()
    {
        RuleFor(request => request.SellerSlug)
            .NotEmpty()
            .MaximumLength(120)
            .Must(slug => Slugs.Slugify(slug) == slug).WithMessage("'Seller Slug' must be lowercase kebab-case.");

        RuleFor(request => request.Items).NotEmpty();

        // An oversized batch is answered with Catalog.BatchTooLarge by the handler; do not validate thousands of items first.
        RuleForEach(request => request.Items)
            .NotNull()
            .SetValidator(new UpsertOfferItemValidator())
            .When(request => request.Items is { Count: <= UpsertLimits.MaxItems });
    }
}

internal sealed class UpsertOfferItemValidator : AbstractValidator<UpsertOfferItem>
{
    private const int MaxSizes = 50;

    private const int MaxImages = 10;

    public UpsertOfferItemValidator()
    {
        RuleFor(item => item.ExternalId).NotEmpty().MaximumLength(200);
        RuleFor(item => item.Url).NotEmpty().MaximumLength(2048).Must(IsHttpUrl).WithMessage("'Url' must be an absolute http or https URL.");
        RuleFor(item => item.Title).NotEmpty().MaximumLength(300);

        RuleFor(item => item.Price).GreaterThan(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(item => item.OriginalPrice).GreaterThan(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(item => item.ShippingCost).GreaterThanOrEqualTo(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(item => item.FreeShippingThreshold).GreaterThanOrEqualTo(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);

        RuleFor(item => item.Currency!)
            .Must(currency => currency.Length == 3 && currency.All(char.IsAsciiLetter))
            .WithMessage("'Currency' must be a three-letter code such as TRY.")
            .When(item => item.Currency is not null);

        RuleFor(item => item.StockStatus)
            .Must(EnumNames.IsValid<StockStatus>)
            .WithMessage(OneOf<StockStatus>("Stock Status"));

        RuleFor(item => item.AvailableSizes!)
            .Must(sizes => sizes.Count <= MaxSizes)
            .WithMessage($"'Available Sizes' must not contain more than {MaxSizes} entries.")
            .When(item => item.AvailableSizes is not null);
        RuleForEach(item => item.AvailableSizes).NotEmpty().MaximumLength(20);

        RuleFor(item => item.ImageUrls!)
            .Must(urls => urls.Count <= MaxImages)
            .WithMessage($"'Image Urls' must not contain more than {MaxImages} entries.")
            .When(item => item.ImageUrls is not null);
        RuleForEach(item => item.ImageUrls).MaximumLength(2048).Must(IsHttpUrl).WithMessage("'Image Urls' entries must be absolute http or https URLs.");

        RuleFor(item => item.Product).NotNull().SetValidator(new UpsertProductInfoValidator());
    }

    internal static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    internal static string OneOf<TEnum>(string displayName)
        where TEnum : struct, Enum =>
        $"'{displayName}' must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.";
}

internal sealed class UpsertProductInfoValidator : AbstractValidator<UpsertProductInfo>
{
    public UpsertProductInfoValidator()
    {
        RuleFor(product => product.BrandName)
            .NotEmpty()
            .MaximumLength(100)
            .Must(name => Slugs.Slugify(name).Length > 0).WithMessage("'Brand Name' must contain at least one letter or digit.");

        RuleFor(product => product.Name)
            .NotEmpty()
            .MaximumLength(200)
            .Must(name => Slugs.Slugify(name).Length > 0).WithMessage("'Name' must contain at least one letter or digit.");

        RuleFor(product => product.ModelCode).MaximumLength(100).Must(code => !string.IsNullOrWhiteSpace(code)).When(product => product.ModelCode is not null);
        RuleFor(product => product.CategorySlug).NotEmpty().MaximumLength(100);

        RuleFor(product => product.Audience).Must(EnumNames.IsValid<Audience>).WithMessage(UpsertOfferItemValidator.OneOf<Audience>("Audience"));
        RuleFor(product => product.PrimaryColor).Must(EnumNames.IsValid<ProductColor>).WithMessage(UpsertOfferItemValidator.OneOf<ProductColor>("Primary Color"));
        RuleFor(product => product.Pattern).Must(EnumNames.IsValid<ProductPattern>).WithMessage(UpsertOfferItemValidator.OneOf<ProductPattern>("Pattern")).When(product => product.Pattern is not null);
        RuleFor(product => product.Fit).Must(EnumNames.IsValid<ProductFit>).WithMessage(UpsertOfferItemValidator.OneOf<ProductFit>("Fit")).When(product => product.Fit is not null);
        RuleFor(product => product.Formality).InclusiveBetween(1, 5);

        RuleForEach(product => product.Seasons)
            .Must(EnumNames.IsValidSeason)
            .WithMessage("'Seasons' entries must be one of: Spring, Summer, Autumn, Winter.");

        RuleFor(product => product.Material).MaximumLength(200);
        RuleFor(product => product.Description).MaximumLength(4000);
        RuleFor(product => product.CanonicalProductId).NotEqual(Guid.Empty);
    }
}
