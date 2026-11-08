using Kombin.Api.Common.Time;
using Microsoft.IdentityModel.Tokens;

namespace Kombin.Api.Features.Identity.ExternalTokens;

/// <summary>
/// A provider's signing keys (its JWKS document), cached for <see cref="CacheLifetime"/>. A token signed with a key ID the cache does not
/// know triggers one refresh, because providers rotate keys without notice; refreshes are at least <see cref="MinimumRefreshInterval"/>
/// apart so tokens with made-up key IDs cannot turn us into a request amplifier against the provider. If a refresh fails the last keys
/// are kept. One instance per provider, held by its singleton verifier.
/// </summary>
internal sealed partial class JwksKeySource(Uri jwksUri, IHttpClientFactory httpClientFactory, IClock clock, ILogger logger) : IDisposable
{
    public const string HttpClientName = "identity-jwks";

    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    internal static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // One immutable snapshot swapped as a whole, so readers outside the gate never see keys from one fetch with the time of another.
    private volatile Snapshot? _snapshot;

    /// <summary>The keys to validate a token whose header names <paramref name="keyId"/>; null when they have never been fetched.</summary>
    public async Task<IReadOnlyList<SecurityKey>?> GetKeysAsync(string? keyId, CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;

        if (snapshot is not null && IsFresh(snapshot) && snapshot.Knows(keyId))
        {
            return snapshot.Keys;
        }

        await _refreshGate.WaitAsync(cancellationToken);

        try
        {
            // Another request may have refreshed while this one waited.
            snapshot = _snapshot;

            if (snapshot is not null && IsFresh(snapshot) && (snapshot.Knows(keyId) || clock.UtcNow - snapshot.FetchedAt < MinimumRefreshInterval))
            {
                return snapshot.Keys;
            }

            var keys = await FetchAsync(cancellationToken);

            if (keys is null)
            {
                return snapshot?.Keys;
            }

            _snapshot = new Snapshot(keys, clock.UtcNow);
            return keys;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose() => _refreshGate.Dispose();

    private bool IsFresh(Snapshot snapshot) => clock.UtcNow - snapshot.FetchedAt < CacheLifetime;

    private async Task<IReadOnlyList<SecurityKey>?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(jwksUri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                LogFetchFailed(logger, jwksUri, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var keys = new JsonWebKeySet(json).GetSigningKeys();

            if (keys.Count == 0)
            {
                LogFetchFailed(logger, jwksUri, "no signing keys");
                return null;
            }

            return [.. keys];
        }
        catch (Exception exception) when (exception is HttpRequestException or ArgumentException
                                              || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogFetchFailed(logger, jwksUri, exception.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetching the signing keys from {JwksUri} failed ({Reason}); the cached keys, if any, stay in use.")]
    private static partial void LogFetchFailed(ILogger logger, Uri jwksUri, string reason);

    private sealed record Snapshot(IReadOnlyList<SecurityKey> Keys, DateTimeOffset FetchedAt)
    {
        public bool Knows(string? keyId) => keyId is null || Keys.Any(key => key.KeyId == keyId);
    }
}
