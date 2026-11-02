using FluentValidation;
using Kombin.Api.Common.Storage;

namespace Kombin.Api.Features.Wardrobe.CreateUploadUrl;

internal sealed class CreateUploadUrlValidator : AbstractValidator<CreateUploadUrlRequest>
{
    public CreateUploadUrlValidator()
    {
        RuleFor(request => request.ContentType)
            .NotEmpty()
            .Must(contentType => StorageKeys.ExtensionFor(contentType) is not null)
            .WithMessage($"'Content Type' must be one of: {string.Join(", ", StorageKeys.ImageContentTypes)}.");

        RuleFor(request => request.SizeBytes)
            .InclusiveBetween(1, WardrobeLimits.MaxImageBytes)
            .WithMessage($"'Size Bytes' must be between 1 and {WardrobeLimits.MaxImageBytes} (8 MB).");
    }
}
