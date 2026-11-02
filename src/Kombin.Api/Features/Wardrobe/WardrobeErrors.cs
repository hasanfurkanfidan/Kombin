using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Wardrobe;

internal static class WardrobeErrors
{
    // Also the answer for someone else's item: whether it exists is none of the caller's business.
    public static readonly Error ItemNotFound =
        Error.NotFound("Wardrobe.ItemNotFound", "The wardrobe item does not exist.");

    public static readonly Error UploadMissing =
        Error.Conflict("Wardrobe.UploadMissing", "The photo has not been uploaded yet; PUT it to the upload URL first.");

    public static readonly Error ImageTooLarge =
        Error.Validation("Wardrobe.ImageTooLarge", $"The photo must not be larger than {WardrobeLimits.MaxImageBytes / (1024 * 1024)} MB.");

    public static readonly Error NothingToProcess =
        Error.Conflict("Wardrobe.NothingToProcess", "The item has no photo to analyse.");

    public static readonly Error ItemProcessing =
        Error.Conflict("Wardrobe.ItemProcessing", "The item is still being analysed.");

    public static readonly Error ItemArchived =
        Error.Conflict("Wardrobe.ItemArchived", "The item has been removed from the wardrobe.");

    public static readonly Error UnknownCategory =
        Error.Validation("Wardrobe.UnknownCategory", "The category does not exist.");

    public static readonly Error UnknownBrand =
        Error.Validation("Wardrobe.UnknownBrand", "The brand does not exist.");
}

internal static class WardrobeLimits
{
    /// <summary>The largest photo accepted, the same limit as the vision service's.</summary>
    public const long MaxImageBytes = 8 * 1024 * 1024;

    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan OriginalUrlLifetime = TimeSpan.FromMinutes(15);

    /// <summary>A <c>process</c> call this soon after the last one does not enqueue the analysis again.</summary>
    public static readonly TimeSpan ReprocessAfter = TimeSpan.FromMinutes(10);

    /// <summary>The storage area of wardrobe images: keys are <c>wardrobe/{userId}/{itemId}/{variant}.{ext}</c>.</summary>
    public const string StorageArea = "wardrobe";
}
