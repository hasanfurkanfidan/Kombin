using FluentValidation;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League.Report;

internal sealed class ReportEntryRequestValidator : AbstractValidator<ReportEntryRequest>
{
    public ReportEntryRequestValidator()
    {
        RuleFor(request => request.Side)
            .NotEmpty()
            .Must(LeagueChoices.IsSide)
            .WithMessage("'Side' must be left or right.");

        RuleFor(request => request.Reason)
            .Must(reason => reason is null || LeagueChoices.TryParse<LeagueReportReason>(reason, out _))
            .WithMessage($"'Reason' must be one of: {string.Join(", ", Enum.GetNames<LeagueReportReason>())}.");
    }
}
