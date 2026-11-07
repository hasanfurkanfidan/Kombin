using FluentValidation;

namespace Kombin.Api.Features.Social.GetFeed;

internal sealed class FeedQueryValidator : AbstractValidator<FeedQuery>
{
    /// <summary>The ranked list holds at most ~230 posts: 20 pages is beyond its end already.</summary>
    public const int MaxPage = 20;

    public FeedQueryValidator()
    {
        RuleFor(query => query.Mode)
            .Must(mode => mode is null
                || string.Equals(mode, GetFeedEndpoint.ForYou, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, GetFeedEndpoint.Following, StringComparison.OrdinalIgnoreCase))
            .WithMessage($"'Mode' must be '{GetFeedEndpoint.ForYou}' or '{GetFeedEndpoint.Following}'.");

        RuleFor(query => query.Page).InclusiveBetween(1, MaxPage);
    }
}
