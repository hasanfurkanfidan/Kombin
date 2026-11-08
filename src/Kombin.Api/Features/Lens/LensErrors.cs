using Kombin.Api.Common.Results;
using Kombin.Api.Features.Wardrobe;

namespace Kombin.Api.Features.Lens;

internal static class LensErrors
{
    public static readonly Error ImageMissing =
        Error.Validation("Lens.ImageMissing", "Send the photo as the multipart field 'image'.");

    public static readonly Error ImageTooLarge =
        Error.Validation("Lens.ImageTooLarge", $"The photo must not be larger than {WardrobeLimits.MaxImageBytes / (1024 * 1024)} MB.");

    public static readonly Error UnsupportedImage =
        Error.Validation("Lens.UnsupportedImage", "The photo must be a JPEG, PNG, WEBP or HEIC image.");

    public static readonly Error InvalidAudience =
        Error.Validation("Lens.InvalidAudience", "audience must be Women, Men, Unisex, Kids or all.");

    public static readonly Error VisionUnavailable =
        Error.Unavailable("Lens.VisionUnavailable", "The photo cannot be analysed right now; try again in a moment.");
}
