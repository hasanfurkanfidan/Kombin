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
    /// What a public image (<c>cutout.*</c>, <c>thumb.*</c>) says to every cache: its key is never written with different bytes once a client can see
    /// it (a wardrobe item's images are final when it is Ready, a league copy has a random key), so a phone keeps it for a year and never asks again.
    /// </summary>
    public const string LongLivedCacheControl = "public, max-age=31536000, immutable";

    /// <summary>
    /// A league copy is the picture of one day's outfit and is deleted when the outfit is withdrawn or after seven days; a cache must not hold it
    /// much longer than the day it is voted on, so it is cached for a day only.
    /// </summary>
    public const string LeagueCacheControl = "public, max-age=86400, immutable";

    /// <summary>
    /// Everything that is not public (originals, a post's feed image and preview, a try-on cutout and render): served through presigned GETs, to the
    /// owner or a signed-in viewer only, so no shared cache may keep it and a device is told not to store it either.
    /// </summary>
    public const string PrivateCacheControl = "private, no-store";

    /// <summary>The <c>Cache-Control</c> an object is stored with, decided by its key like <see cref="IsPublic"/> (the league's copies live under <c>league/</c>).</summary>
    public static string CacheControlFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!IsPublic(key))
        {
            return PrivateCacheControl;
        }

        return key.StartsWith("league/", StringComparison.Ordinal) ? LeagueCacheControl : LongLivedCacheControl;
    }

    /// <summary>Whether the bucket policy lets anyone read the key: its file name is <c>cutout.*</c> or <c>thumb.*</c>.</summary>
    public static bool IsPublic(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var name = key[(key.LastIndexOf('/') + 1)..];

        return name.StartsWith("cutout.", StringComparison.Ordinal) || name.StartsWith("thumb.", StringComparison.Ordinal);
    }
}
