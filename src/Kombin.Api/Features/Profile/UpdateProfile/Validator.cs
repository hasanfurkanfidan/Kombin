using FluentValidation;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Profile.UpdateProfile;

internal sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileRequest>
{
    private const int MinBirthYear = 1920;

    private const int MinAge = 10;

    public UpdateProfileValidator(IClock clock)
    {
        RuleFor(request => request.Audience)
            .Must(audience => EnumNames.TryParse<Audience>(audience, out var parsed) && parsed != Audience.Kids)
            .WithMessage("'Audience' must be one of: Women, Men, Unisex.")
            .When(request => request.Audience is not null);

        var maxBirthYear = clock.UtcNow.Year - MinAge;
        RuleFor(request => request.BirthYear).InclusiveBetween(MinBirthYear, maxBirthYear);

        RuleFor(request => request.HeightCm).InclusiveBetween(120, 230);
        RuleFor(request => request.WeightKg).InclusiveBetween(30m, 250m).PrecisionScale(4, 1, ignoreTrailingZeros: true);
        RuleFor(request => request.ChestCm).InclusiveBetween(50, 200);
        RuleFor(request => request.WaistCm).InclusiveBetween(50, 200);
        RuleFor(request => request.HipCm).InclusiveBetween(50, 200);
        RuleFor(request => request.ShoeSizeEu).InclusiveBetween(30m, 52m).PrecisionScale(4, 1, ignoreTrailingZeros: true);
    }
}
