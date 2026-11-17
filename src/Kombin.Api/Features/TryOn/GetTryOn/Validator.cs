using FluentValidation;

namespace Kombin.Api.Features.TryOn.GetTryOn;

internal sealed class TryOnQueryValidator : AbstractValidator<TryOnQuery>
{
    public TryOnQueryValidator()
    {
        RuleFor(query => query)
            .Must(query => (query.OutfitId is null) != (query.ProductId is null))
            .OverridePropertyName("outfitId")
            .WithMessage("Exactly one of 'outfitId' and 'productId' is required.");

        RuleFor(query => query.WithWardrobe)
            .Null()
            .When(query => query.OutfitId is not null)
            .WithMessage("'withWardrobe' goes with 'productId' only.");
    }
}
