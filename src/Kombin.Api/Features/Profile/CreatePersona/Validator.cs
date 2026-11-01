using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Profile.CreatePersona;

internal sealed class CreatePersonaValidator : AbstractValidator<CreatePersonaRequest>
{
    private const int MaxColors = 15;

    public CreatePersonaValidator()
    {
        RuleFor(request => request.Name).NotEmpty().Must(name => name is null || name.Trim().Length <= 40).WithMessage("'Name' must be 40 characters or fewer.");
        RuleFor(request => request.Icon).NotEmpty().Must(icon => icon is null || icon.Trim().Length <= 40).WithMessage("'Icon' must be 40 characters or fewer.");

        RuleFor(request => request.BudgetMin).GreaterThanOrEqualTo(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(request => request.BudgetMax).GreaterThanOrEqualTo(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(request => request.BudgetMax)
            .GreaterThanOrEqualTo(request => request.BudgetMin!.Value)
            .WithMessage("'Budget Max' must not be below 'Budget Min'.")
            .When(request => request.BudgetMin is not null && request.BudgetMax is not null);

        RuleFor(request => request.FormalityMin).InclusiveBetween(1, 5);
        RuleFor(request => request.FormalityMax).InclusiveBetween(1, 5);
        // A missing bound defaults to the full range (1 and 5), so the check also covers a single given bound.
        RuleFor(request => request)
            .Must(request => (request.FormalityMin ?? 1) <= (request.FormalityMax ?? 5))
            .OverridePropertyName("formalityMax")
            .WithMessage("'Formality Max' must not be below 'Formality Min'.");

        RuleFor(request => request.PreferredColors!)
            .Must(colors => colors.Count <= MaxColors && PersonaPreferences.AreAll<ProductColor>(colors))
            .WithMessage($"'Preferred Colors' must be at most {MaxColors} of: {string.Join(", ", Enum.GetNames<ProductColor>())}.")
            .When(request => request.PreferredColors is not null);
        RuleFor(request => request.AvoidedColors!)
            .Must(colors => colors.Count <= MaxColors && PersonaPreferences.AreAll<ProductColor>(colors))
            .WithMessage($"'Avoided Colors' must be at most {MaxColors} of: {string.Join(", ", Enum.GetNames<ProductColor>())}.")
            .When(request => request.AvoidedColors is not null);
        RuleFor(request => request)
            .Must(request => !PersonaPreferences.Colors(request.PreferredColors ?? []).Intersect(PersonaPreferences.Colors(request.AvoidedColors ?? []), StringComparer.Ordinal).Any())
            .OverridePropertyName("avoidedColors")
            .WithMessage("A color cannot be both preferred and avoided.");

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
