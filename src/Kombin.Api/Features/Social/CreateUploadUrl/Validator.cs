using FluentValidation;

namespace Kombin.Api.Features.Social.CreateUploadUrl;

internal sealed class CreatePostUploadUrlValidator : AbstractValidator<CreatePostUploadUrlRequest>
{
    public CreatePostUploadUrlValidator()
    {
        RuleFor(request => request.ContentType)
            .NotEmpty()
            .Must(contentType => SocialLimits.ImageContentTypes.Contains(contentType?.Split(';')[0].Trim().ToLowerInvariant()))
            .WithMessage($"'Content Type' must be one of: {string.Join(", ", SocialLimits.ImageContentTypes)}.");

        RuleFor(request => request.SizeBytes)
            .InclusiveBetween(1, SocialLimits.MaxImageBytes)
            .WithMessage($"'Size Bytes' must be between 1 and {SocialLimits.MaxImageBytes} (8 MB).");
    }
}
