using FluentValidation;
using Kombin.Api.Persistence.Entities.TryOn;

namespace Kombin.Api.Features.TryOn.ReportTryOn;

internal sealed class ReportTryOnRequestValidator : AbstractValidator<ReportTryOnRequest>
{
    public ReportTryOnRequestValidator()
    {
        RuleFor(request => request)
            .Must(request => (request.OutfitId is null) != (request.ProductId is null))
            .OverridePropertyName("outfitId")
            .WithMessage("Exactly one of 'outfitId' and 'productId' is required.");

        RuleFor(request => request.GarmentIds)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(ids => ids!.Count is >= 1 and <= ReportTryOnEndpoint.MaxGarments)
            .WithMessage($"Between 1 and {ReportTryOnEndpoint.MaxGarments} garment IDs.");

        RuleForEach(request => request.GarmentIds).NotEmpty();

        RuleFor(request => request.Renderer)
            .NotEmpty()
            .MaximumLength(TryOnRender.MaxRendererLength);
    }
}
