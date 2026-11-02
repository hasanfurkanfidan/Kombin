using FluentValidation;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.AddFromProduct;

internal sealed class AddFromProductValidator : AbstractValidator<AddFromProductRequest>
{
    public AddFromProductValidator()
    {
        RuleFor(request => request.ProductId).NotEmpty();
        RuleFor(request => request.Size).MaximumLength(WardrobeItem.MaxSizeLength);
    }
}
