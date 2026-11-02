namespace Kombin.Api.Common.Storage;

/// <summary>
/// The S3-compatible object store (MinIO locally, S3 or R2 later). <c>PresignEndpoint</c> is the address the app uploads to; it defaults
/// to <c>Endpoint</c> and differs only when the API reaches the store under another name than the phone does (compose: <c>minio:9000</c>
/// inside, <c>localhost:9000</c> outside). A presigned URL is signed for its host, so it cannot be rewritten afterwards.
/// </summary>
internal sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Endpoint { get; set; } = string.Empty;

    public string? PresignEndpoint { get; set; }

    public string Bucket { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Where public objects are read from, bucket included, e.g. <c>http://localhost:9000/kombin</c> or a CDN origin.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>True for MinIO (<c>host/bucket/key</c>); false for virtual-hosted buckets (<c>bucket.host/key</c>).</summary>
    public bool ForcePathStyle { get; set; } = true;

    public string Region { get; set; } = "us-east-1";

    /// <summary>The settings a working store needs; checked when the S3 client is first created, not at start-up (tests replace the store).</summary>
    public IEnumerable<string> MissingSettings()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            yield return nameof(Endpoint);
        }

        if (string.IsNullOrWhiteSpace(Bucket))
        {
            yield return nameof(Bucket);
        }

        if (string.IsNullOrWhiteSpace(AccessKey))
        {
            yield return nameof(AccessKey);
        }

        if (string.IsNullOrWhiteSpace(SecretKey))
        {
            yield return nameof(SecretKey);
        }

        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _))
        {
            yield return nameof(PublicBaseUrl);
        }
    }
}
