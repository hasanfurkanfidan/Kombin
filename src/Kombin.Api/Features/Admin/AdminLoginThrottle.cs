using System.Collections.Concurrent;
using Kombin.Api.Common.Time;

namespace Kombin.Api.Features.Admin;

/// <summary>
/// The admin login's brake: 5 failed sign-ins within 15 minutes lock that client address out for the rest of the window (a successful
/// sign-in clears it). In memory and per process, which is enough for one API instance; a restart forgives everyone. Behind a reverse proxy
/// every request has the proxy's address until forwarded headers are configured (Task 16), so the lock would then be shared by all callers.
/// </summary>
internal sealed class AdminLoginThrottle(IClock clock)
{
    public const int MaxFailures = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private const int CleanupThreshold = 1000;

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    /// <summary>Whether <paramref name="client"/> has used up its failures in the current window.</summary>
    public bool IsLocked(string client)
    {
        if (!_failures.TryGetValue(client, out var list))
        {
            return false;
        }

        lock (list)
        {
            Prune(list, clock.UtcNow);

            return list.Count >= MaxFailures;
        }
    }

    public void RegisterFailure(string client)
    {
        var now = clock.UtcNow;
        var list = _failures.GetOrAdd(client, _ => []);

        lock (list)
        {
            Prune(list, now);
            list.Add(now);
        }

        if (_failures.Count > CleanupThreshold)
        {
            Cleanup(now);
        }
    }

    public void Reset(string client) => _failures.TryRemove(client, out _);

    private static void Prune(List<DateTimeOffset> list, DateTimeOffset now) => list.RemoveAll(at => at <= now - Window);

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var (client, list) in _failures)
        {
            lock (list)
            {
                Prune(list, now);

                if (list.Count == 0)
                {
                    _failures.TryRemove(client, out _);
                }
            }
        }
    }
}
