using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Kombin.Api.Common.Storage;

/// <summary>
/// <see cref="IObjectStorage"/> over the S3 API, which MinIO, AWS S3 and Cloudflare R2 all speak. A singleton: the clients are
/// thread-safe and keep their connections. Presigning uses a second client bound to <see cref="StorageOptions.PresignEndpoint"/>;
/// presigning is local computation, that client never makes a call.
/// </summary>
internal sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly StorageOptions _options;

    private readonly AmazonS3Client _client;

    private readonly AmazonS3Client _presigner;

    private readonly Protocol _presignProtocol;

    public S3ObjectStorage(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var missing = options.MissingSettings().ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Object storage is not configured: set {string.Join(", ", missing.Select(name => $"{StorageOptions.SectionName}:{name}"))}.");
        }

        _options = options;

        var presignEndpoint = string.IsNullOrWhiteSpace(options.PresignEndpoint) ? options.Endpoint : options.PresignEndpoint;
        _presignProtocol = new Uri(presignEndpoint).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;

        var credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
        _client = new AmazonS3Client(credentials, CreateConfig(options.Endpoint, options));
        _presigner = new AmazonS3Client(credentials, CreateConfig(presignEndpoint, options));
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);

        await _client.PutObjectAsync(
            new PutObjectRequest { BucketName = _options.Bucket, Key = key, InputStream = stream, ContentType = contentType, AutoCloseStream = false },
            cancellationToken);
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _options.Bucket, Key = key }, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);

            return new StoredObject(buffer.ToArray(), response.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = key }, cancellationToken);

            return metadata.ContentLength;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public PresignedUpload GetPresignedUploadUrl(string key, string contentType, TimeSpan lifetime)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);

        // The content type is part of the signature, so the client must send exactly this header.
        var url = _presigner.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
            Protocol = _presignProtocol,
        });

        return new PresignedUpload(url, new Dictionary<string, string> { ["Content-Type"] = contentType }, expiresAt);
    }

    public string GetPresignedDownloadUrl(string key, TimeSpan lifetime) =>
        _presigner.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = _presignProtocol,
        });

    public string GetPublicUrl(string key) => $"{_options.PublicBaseUrl.TrimEnd('/')}/{key}";

    public async Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        // DeleteObjects takes at most 1000 keys and reports missing ones as deleted.
        foreach (var chunk in keys.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            await _client.DeleteObjectsAsync(
                new DeleteObjectsRequest
                {
                    BucketName = _options.Bucket,
                    Objects = [.. chunk.Select(key => new KeyVersion { Key = key })],
                    Quiet = true,
                },
                cancellationToken);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _presigner.Dispose();
    }

    private static AmazonS3Config CreateConfig(string endpoint, StorageOptions options) => new()
    {
        ServiceURL = endpoint,
        ForcePathStyle = options.ForcePathStyle,
        AuthenticationRegion = options.Region,

        // The SDK's default checksums (CRC on every request) are not understood by every S3-compatible store; S3 does not need them.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        Timeout = TimeSpan.FromSeconds(30),
    };
}
