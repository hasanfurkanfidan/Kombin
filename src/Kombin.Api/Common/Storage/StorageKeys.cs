namespace Kombin.Api.Common.Storage;

/// <summary>
/// The renditions of one stored image. Only <c>Cutout</c> and <c>Thumb</c> are public; an original stays private, and so do a social post's
/// <c>Feed</c> image and <c>Preview</c> (a followers-only post must not be readable by URL), which are served through presigned GETs, a try-on
/// garment's <c>Prepared</c> cutout (read by the server only) and a try-on <c>Render</c> (it reveals body proportions: presigned GETs only).
/// </summary>
internal enum ImageVariant
{
    Original,
    Cutout,
    Thumb,
    Feed,
    Preview,
    Prepared,
    Render,
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

    /// <summary>
    /// What a public image (<c>cutout.*</c>, <c>thumb.*</c>, a wardrobe item's or a league copy) says to every cache: a day, never <c>immutable</c> and never
    /// longer (task B9). A public URL cannot be revoked, so the longest a deleted account's or item's photo can stay in a CDN or on another phone is the
    /// time we told them to keep it; the app keeps its own image cache and clears it on sign-out. A league copy is also the picture of one day, deleted
    /// when its outfit is withdrawn and after seven days.
    /// </summary>
    public const string PublicCacheControl = "public, max-age=86400";

    /// <summary>
    /// Everything that is not public (originals, a post's feed image and preview, a try-on cutout and render): served through presigned GETs, to the
    /// owner or a signed-in viewer only, so no shared cache may keep it and a device is told not to store it either.
    /// </summary>
    public const string PrivateCacheControl = "private, no-store";

    /// <summary>The <c>Cache-Control</c> an object is stored with, decided by its key like <see cref="IsPublic"/>.</summary>
    public static string CacheControlFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return IsPublic(key) ? PublicCacheControl : PrivateCacheControl;
    }

    /// <summary>Whether the bucket policy lets anyone read the key: its file name is <c>cutout.*</c> or <c>thumb.*</c>.</summary>
    public static bool IsPublic(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var name = key[(key.LastIndexOf('/') + 1)..];

        return name.StartsWith("cutout.", StringComparison.Ordinal) || name.StartsWith("thumb.", StringComparison.Ordinal);
    }
}
