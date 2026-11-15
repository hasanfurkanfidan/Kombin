using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Profile.UpsertSize;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile.AnswerSizePrompt;

internal sealed class AnswerSizePromptValidator : AbstractValidator<AnswerSizePromptRequest>
{
    public AnswerSizePromptValidator()
    {
        RuleFor(request => request.Answer)
            .NotEmpty()
            .Must(answer => EnumNames.TryParse<SizePromptAnswer>(answer, out _))
            .WithMessage($"'Answer' must be one of: {string.Join(", ", Enum.GetNames<SizePromptAnswer>())}.");

        // "Fits" is the one answer that stores a size, so it has to name it; the others may name the size that was bought.
        RuleFor(request => request.Size)
            .NotEmpty()
            .When(request => EnumNames.TryParse<SizePromptAnswer>(request.Answer, out var answer) && answer == SizePromptAnswer.BoughtFits)
            .WithMessage("'Size' is required when the size fitted.");

        RuleFor(request => request.Size!)
            .Must(size => size.Trim().Length <= UpsertSizeValidator.MaxSizeLength)
            .WithMessage($"'Size' must be {UpsertSizeValidator.MaxSizeLength} characters or fewer.")
            .When(request => !string.IsNullOrWhiteSpace(request.Size));
    }
}
