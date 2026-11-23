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

    private readonly ConcurrentDictionary<string, DateTimeOffset> _written = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _failingPrefixes = new(StringComparer.Ordinal);

    /// <summary>Makes <see cref="DeleteAsync"/> throw (like an unreachable store) while any key it is given starts with <paramref name="prefix"/>.</summary>
    public void FailDeletesUnder(string prefix) => _failingPrefixes[prefix] = 0;

    public void StopFailingDeletes(string prefix) => _failingPrefixes.TryRemove(prefix, out _);

    private readonly ConcurrentDictionary<string, Func<string, bool>> _failingPuts = new(StringComparer.Ordinal);

    /// <summary>Makes <see cref="PutAsync"/> throw (a full or unreachable store) for every key <paramref name="matches"/>; <paramref name="name"/> turns it off again.</summary>
    public void FailPuts(string name, Func<string, bool> matches) => _failingPuts[name] = matches;

    public void StopFailingPuts(string name) => _failingPuts.TryRemove(name, out _);

    private readonly ConcurrentDictionary<string, StorageHold> _holds = new(StringComparer.Ordinal);

    /// <summary>
    /// Parks the first <see cref="PutAsync"/> of a key, or <see cref="ListAsync"/> of a prefix, that <paramref name="matches"/> until the hold is released
    /// (the caller's request is then deterministically inside its work); a cancelled request makes the parked call throw, as the S3 client does.
    /// </summary>
    public StorageHold Hold(string name, Func<string, bool> matches) => _holds[name] = new StorageHold(matches);

    private async Task WaitIfHeldAsync(string keyOrPrefix, CancellationToken cancellationToken)
    {
        foreach (var (name, hold) in _holds)
        {
            if (hold.Matches(keyOrPrefix) && _holds.TryRemove(name, out _))
            {
                hold.Arrive();
                await hold.Released.WaitAsync(cancellationToken);
            }
        }
    }

    /// <summary>Every key passed to <see cref="DeleteAsync"/>, oldest first.</summary>
    public IReadOnlyList<string> Deleted => [.. _deleted];

    /// <summary>What the app does with the presigned URL: put the photo under the key.</summary>
    public void Upload(string key, byte[] content, string contentType)
    {
        _objects[key] = new StoredObject(content, contentType);
        _written[key] = TestClock.UtcNow;
    }

    /// <summary>Makes an object look as if it had been written at <paramref name="at"/> (what <see cref="ListAsync"/> reports).</summary>
    public void Backdate(string key, DateTimeOffset at) => _written[key] = at;

    /// <summary>The keys of the objects that belong to a user (every key carries the user's ID).</summary>
    public IReadOnlyList<string> KeysOfUser(Guid userId) => [.. _objects.Keys.Where(key => key.Contains($"/{userId}/", StringComparison.Ordinal))];

    public StoredObject? Find(string key) => _objects.GetValueOrDefault(key);

    /// <summary>The <c>Cache-Control</c> the S3 store would have written with the object (<see cref="StorageKeys.CacheControlFor"/>), or null when there is no such object.</summary>
    public string? CacheControlOf(string key) => _objects.ContainsKey(key) ? StorageKeys.CacheControlFor(key) : null;

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        await WaitIfHeldAsync(key, cancellationToken);
        if (_failingPuts.Values.Any(matches => matches(key)))
        {
            throw new IOException("The object store is unreachable (simulated).");
        }

        _objects[key] = new StoredObject(content, contentType);
        _written[key] = TestClock.UtcNow;
    }

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Find(key));

    public Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Find(key)?.Content.LongLength);

    public PresignedUpload GetPresignedUploadUrl(string key, string contentType, TimeSpan lifetime) =>
        new($"https://storage.test/kombin/{key}?X-Amz-Signature=fake", new Dictionary<string, string> { ["Content-Type"] = contentType }, TestClock.UtcNow.Add(lifetime));

    public string GetPresignedDownloadUrl(string key, TimeSpan lifetime) => $"https://storage.test/kombin/{key}?X-Amz-Signature=fake-get";

    public string GetPublicUrl(string key) => $"{PublicBaseUrl}/{key}";

    public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Any(key => _failingPrefixes.Keys.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))))
        {
            throw new IOException("The object store is unreachable (simulated).");
        }

        foreach (var key in keys)
        {
            _objects.TryRemove(key, out _);
            _written.TryRemove(key, out _);
            _deleted.Enqueue(key);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await WaitIfHeldAsync(prefix, cancellationToken);
        foreach (var key in _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new StoredObjectInfo(key, _written.GetValueOrDefault(key, TestClock.UtcNow));
        }
    }
}

/// <summary>A parked storage call (<see cref="FakeObjectStorage.Hold"/>): <see cref="Reached"/> completes when it is parked, <see cref="Release"/> lets it go on.</summary>
internal sealed class StorageHold(Func<string, bool> matches)
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task;

    internal Task Released => _released.Task;

    internal bool Matches(string keyOrPrefix) => matches(keyOrPrefix);

    internal void Arrive() => _reached.TrySetResult();

    public void Release() => _released.TrySetResult();
}
