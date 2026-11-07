using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.PublishPost;

internal sealed class PublishPostValidator : AbstractValidator<PublishPostRequest>
{
    public PublishPostValidator()
    {
        RuleFor(request => request.Caption).MaximumLength(Post.MaxCaptionLength);

        RuleFor(request => request.Visibility)
            .NotEmpty()
            .Must(EnumNames.IsValid<PostVisibility>)
            .WithMessage($"'Visibility' must be one of: {string.Join(", ", Enum.GetNames<PostVisibility>())}.");

        RuleFor(request => request.Tags)
            .NotNull()
            .WithMessage("'Tags' is required; send an empty list for no tags.")
            .Must(tags => tags is null || tags.Count <= SocialLimits.MaxTags)
            .WithMessage($"A post has at most {SocialLimits.MaxTags} tags.");

        RuleForEach(request => request.Tags).ChildRules(tag =>
        {
            tag.RuleFor(item => item.Kind)
                .NotEmpty()
                .Must(EnumNames.IsValid<PostTagKind>)
                .WithMessage($"'Kind' must be one of: {string.Join(", ", Enum.GetNames<PostTagKind>())}.");

            tag.RuleFor(item => item.WardrobeItemId)
                .NotNull()
                .When(item => Is(item.Kind, PostTagKind.Wardrobe))
                .WithMessage("A wardrobe tag needs 'wardrobeItemId'.");

            tag.RuleFor(item => item.ProductId)
                .NotNull()
                .When(item => Is(item.Kind, PostTagKind.Product))
                .WithMessage("A product tag needs 'productId'.");

            tag.RuleFor(item => item.Label).MaximumLength(PostTag.MaxLabelLength);

            foreach (var (name, read) in new (string, Func<PublishTag, double>)[] { ("x", item => item.X), ("y", item => item.Y) })
            {
                tag.RuleFor(item => read(item)).InclusiveBetween(0, 1).OverridePropertyName(name);
            }

            foreach (var (name, read) in new (string, Func<PublishTag, double>)[] { ("w", item => item.W), ("h", item => item.H) })
            {
                tag.RuleFor(item => read(item)).GreaterThan(0).LessThanOrEqualTo(1).OverridePropertyName(name);
            }
        });
    }

    private static bool Is(string? kind, PostTagKind expected) => EnumNames.TryParse<PostTagKind>(kind, out var parsed) && parsed == expected;
}
