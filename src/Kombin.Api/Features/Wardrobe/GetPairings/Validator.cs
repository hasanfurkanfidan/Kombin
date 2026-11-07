using FluentValidation;

namespace Kombin.Api.Features.Wardrobe.GetPairings;

internal sealed class PairingsQueryValidator : AbstractValidator<PairingsQuery>
{
    public PairingsQueryValidator() =>
        RuleFor(query => query.Limit).InclusiveBetween(1, GetPairingsEndpoint.MaxLimit);
}
