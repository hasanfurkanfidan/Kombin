using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Profile.UpdatePersona;

internal sealed class UpdatePersonaValidator : AbstractValidator<UpdatePersonaRequest>
{
    private const int MaxColors = 15;

    public UpdatePersonaValidator()
    {
        RuleFor(request => request)
            .Must(request => request.Name is not null || request.Icon is not null || request.SortOrder is not null
                || request.BudgetMin is not null || request.BudgetMax is not null || request.ClearBudget is not null
                || request.FormalityMin is not null || request.FormalityMax is not null
                || request.PreferredColors is not null || request.AvoidedColors is not null || request.PreferredFits is not null
                || request.Seasons is not null)
            .OverridePropertyName("body")
            .WithMessage("At least one field must be given.");

        RuleFor(request => request.Name).NotEmpty().Must(name => name is null || name.Trim().Length <= 40).WithMessage("'Name' must be 40 characters or fewer.")
            .When(request => request.Name is not null);
        RuleFor(request => request.Icon).NotEmpty().Must(icon => icon is null || icon.Trim().Length <= 40).WithMessage("'Icon' must be 40 characters or fewer.")
            .When(request => request.Icon is not null);
        RuleFor(request => request.SortOrder).GreaterThanOrEqualTo(0);

        RuleFor(request => request.BudgetMin).GreaterThanOrEqualTo(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(request => request.BudgetMax).GreaterThanOrEqualTo(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(request => request)
            .Must(request => request.ClearBudget != true || (request.BudgetMin is null && request.BudgetMax is null))
            .OverridePropertyName("clearBudget")
            .WithMessage("'Clear Budget' cannot be combined with a budget bound.");

        RuleFor(request => request.FormalityMin).InclusiveBetween(1, 5);
        RuleFor(request => request.FormalityMax).InclusiveBetween(1, 5);

        RuleFor(request => request.PreferredColors!)
            .Must(colors => colors.Count <= MaxColors && PersonaPreferences.AreAll<ProductColor>(colors))
            .WithMessage($"'Preferred Colors' must be at most {MaxColors} of: {string.Join(", ", Enum.GetNames<ProductColor>())}.")
            .When(request => request.PreferredColors is not null);
        RuleFor(request => request.AvoidedColors!)
            .Must(colors => colors.Count <= MaxColors && PersonaPreferences.AreAll<ProductColor>(colors))
            .WithMessage($"'Avoided Colors' must be at most {MaxColors} of: {string.Join(", ", Enum.GetNames<ProductColor>())}.")
            .When(request => request.AvoidedColors is not null);
        RuleFor(request => request.PreferredFits!)
            .Must(fits => fits.Count <= Enum.GetValues<ProductFit>().Length && PersonaPreferences.AreAll<ProductFit>(fits))
            .WithMessage($"'Preferred Fits' must be of: {string.Join(", ", Enum.GetNames<ProductFit>())}.")
            .When(request => request.PreferredFits is not null);

        RuleFor(request => request.Seasons!)
            .Must(seasons => seasons.Count > 0 && seasons.All(EnumNames.IsValidSeason))
            .WithMessage("'Seasons' must be one or more of: Spring, Summer, Autumn, Winter.")
            .When(request => request.Seasons is not null);
    }
}
