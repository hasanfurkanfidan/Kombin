using FluentValidation;

namespace Kombin.Api.Features.League.Vote;

internal sealed class VoteRequestValidator : AbstractValidator<VoteRequest>
{
    /// <summary>An hour: anything longer is not a measured decision.</summary>
    public const int MaxDecisionMs = 3_600_000;

    public VoteRequestValidator()
    {
        RuleFor(request => request.Choice)
            .NotEmpty()
            .Must(LeagueChoices.IsChoice)
            .WithMessage("'Choice' must be left, right or skip.");

        RuleFor(request => request.DecisionMs)
            .NotNull()
            .InclusiveBetween(0, MaxDecisionMs);
    }
}
