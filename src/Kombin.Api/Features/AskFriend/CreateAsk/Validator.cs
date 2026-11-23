using FluentValidation;

namespace Kombin.Api.Features.AskFriend.CreateAsk;

internal sealed class CreateAskRequestValidator : AbstractValidator<CreateAskRequest>
{
    public CreateAskRequestValidator()
    {
        RuleFor(request => request.OutfitIds)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(ids => ids!.Count is >= 1 and <= CreateAskEndpoint.MaxOutfits)
            .WithMessage($"Send 1 or {CreateAskEndpoint.MaxOutfits} outfits.")
            .Must(ids => ids!.Distinct().Count() == ids!.Count)
            .WithMessage("The outfits must be different.");

        RuleForEach(request => request.OutfitIds).NotEqual(Guid.Empty);
    }
}
