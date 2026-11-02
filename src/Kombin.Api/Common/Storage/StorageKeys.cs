namespace Kombin.Api.Common.Storage;

/// <summary>The renditions of one stored image. Only <c>Cutout</c> and <c>Thumb</c> are public; an original stays private.</summary>
internal enum ImageVariant
{
    Original,
    Cutout,
    Thumb,
}

/// <summary>
/// Object keys are <c>{area}/{userId}/{ownerId}/{variant}.{ext}</c>, e.g. <c>wardrobe/{userId}/{itemId}/thumb.webp</c>. The bucket policy
/// (<c>docker/minio/public-read.json</c>) makes exactly the <c>cutout.*</c> and <c>thumb.*</c> objects anonymously readable, so the
/// variant name is what decides visibility: never store anything private under those names.
/// </summary>
internal static class StorageKeys
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
        ["image/heic"] = "heic",
        ["image/heif"] = "heif",
    };

    /// <summary>The image types the store accepts for uploads and copies.</summary>
    public static IReadOnlyCollection<string> ImageContentTypes => Extensions.Keys;

    public static string Build(string area, Guid userId, Guid ownerId, ImageVariant variant, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return $"{area}/{userId:D}/{ownerId:D}/{variant.ToString().ToLowerInvariant()}.{extension.TrimStart('.').ToLowerInvariant()}";
    }

    /// <summary>The file extension for an accepted image content type (parameters such as <c>; charset</c> are ignored), or null.</summary>
    public static string? ExtensionFor(string? contentType)
    {
        var mediaType = contentType?.Split(';')[0].Trim();

        return mediaType is not null && Extensions.TryGetValue(mediaType, out var extension) ? extension : null;
    }

    /// <summary>Whether the bucket policy lets anyone read the key: its file name is <c>cutout.*</c> or <c>thumb.*</c>.</summary>
    public static bool IsPublic(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var name = key[(key.LastIndexOf('/') + 1)..];

        return name.StartsWith("cutout.", StringComparison.Ordinal) || name.StartsWith("thumb.", StringComparison.Ordinal);
    }
}
