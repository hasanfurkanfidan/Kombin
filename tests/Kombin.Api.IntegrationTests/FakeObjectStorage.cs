using System.Collections.Concurrent;
using Kombin.Api.Common.Storage;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The object store for the integration tests: an in-memory map instead of MinIO. Every key carries a user and item ID, so tests sharing
/// the host never touch each other's objects. The S3 implementation itself is exercised against MinIO in the compose walkthrough.
/// </summary>
internal sealed class FakeObjectStorage : IObjectStorage
{
    public const string PublicBaseUrl = "https://cdn.test/kombin";

    private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<string> _deleted = new();

    /// <summary>Every key passed to <see cref="DeleteAsync"/>, oldest first.</summary>
    public IReadOnlyList<string> Deleted => [.. _deleted];

    /// <summary>What the app does with the presigned URL: put the photo under the key.</summary>
    public void Upload(string key, byte[] content, string contentType) => _objects[key] = new StoredObject(content, contentType);

    public StoredObject? Find(string key) => _objects.GetValueOrDefault(key);

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        _objects[key] = new StoredObject(content, contentType);
        return Task.CompletedTask;
    }

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Find(key));

    public Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Find(key)?.Content.LongLength);

    public PresignedUpload GetPresignedUploadUrl(string key, string contentType, TimeSpan lifetime) =>
        new($"https://storage.test/kombin/{key}?X-Amz-Signature=fake", new Dictionary<string, string> { ["Content-Type"] = contentType }, DateTimeOffset.UtcNow.Add(lifetime));

    public string GetPresignedDownloadUrl(string key, TimeSpan lifetime) => $"https://storage.test/kombin/{key}?X-Amz-Signature=fake-get";

    public string GetPublicUrl(string key) => $"{PublicBaseUrl}/{key}";

    public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            _objects.TryRemove(key, out _);
            _deleted.Enqueue(key);
        }

        return Task.CompletedTask;
    }
}
