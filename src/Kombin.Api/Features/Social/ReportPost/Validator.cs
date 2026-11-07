using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.ReportPost;

internal sealed class ReportPostValidator : AbstractValidator<ReportPostRequest>
{
    public ReportPostValidator()
    {
        RuleFor(request => request.Reason)
            .NotEmpty()
            .Must(EnumNames.IsValid<ReportReason>)
            .WithMessage($"'Reason' must be one of: {string.Join(", ", Enum.GetNames<ReportReason>())}.");
    }
}
