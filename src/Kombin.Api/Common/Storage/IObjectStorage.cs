namespace Kombin.Api.Common.Storage;

/// <summary>A presigned PUT: the client sends the bytes to <c>Url</c> with exactly these <c>Headers</c> before <c>ExpiresAt</c>.</summary>
internal sealed record PresignedUpload(string Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

internal sealed record StoredObject(byte[] Content, string ContentType);

/// <summary>
/// The object store shared by every feature. Keys come from <see cref="StorageKeys"/>; whether a key is publicly readable is decided by
/// its name (the bucket policy, see <see cref="StorageKeys.IsPublic"/>), never per call.
/// </summary>
internal interface IObjectStorage
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken);

    /// <summary>Null when there is no such object.</summary>
    Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>The object's size in bytes, or null when there is no such object.</summary>
    Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken);

    /// <summary>A URL the client can PUT <paramref name="contentType"/> bytes to; the store is not contacted.</summary>
    PresignedUpload GetPresignedUploadUrl(string key, string contentType, TimeSpan lifetime);

    /// <summary>A time-limited GET for an object that is not public (originals).</summary>
    string GetPresignedDownloadUrl(string key, TimeSpan lifetime);

    /// <summary>The permanent URL of a public object. Only meaningful for keys that <see cref="StorageKeys.IsPublic"/> accepts.</summary>
    string GetPublicUrl(string key);

    /// <summary>Deletes the objects; keys that do not exist are ignored.</summary>
    Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}
