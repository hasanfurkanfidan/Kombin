namespace Kombin.Api.Features.Catalog.Embeddings;

/// <summary>
/// The image-download half of the <c>kombin-workers</c> politeness discipline: at most one request per host per <see cref="Interval"/>
/// (default 1 s), never faster, and a host that answers 429/503 with <c>Retry-After</c> gets nothing until that has passed. Pure: the caller
/// asks how long to wait and does the waiting, so the rule is unit-tested without a clock. One instance per job run, like the workers'
/// <c>PoliteClient</c>.
/// </summary>
internal sealed class PoliteSchedule(TimeSpan interval)
{
    private readonly Dictionary<string, DateTimeOffset> _nextSlot = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Interval { get; } = interval;

    /// <summary>Books the host's next slot and returns how long to wait for it from <paramref name="now"/> (zero when it is free).</summary>
    public TimeSpan Reserve(string host, DateTimeOffset now)
    {
        var slot = _nextSlot.TryGetValue(host, out var next) && next > now ? next : now;
        _nextSlot[host] = slot + Interval;

        return slot - now;
    }

    /// <summary>The host asked us to back off: nothing goes to it before <paramref name="now"/> + <paramref name="delay"/>.</summary>
    public void PushBack(string host, DateTimeOffset now, TimeSpan delay)
    {
        var until = now + delay;
        if (!_nextSlot.TryGetValue(host, out var next) || next < until)
        {
            _nextSlot[host] = until;
        }
    }

    /// <summary>
    /// Orders work so consecutive downloads go to different hosts (round-robin over hosts, each host's items in their original order):
    /// with one request per host per second, a run over three hosts then takes a third of the time instead of waiting on one host at a time.
    /// </summary>
    public static List<T> Interleave<T>(IEnumerable<T> items, Func<T, string> host)
    {
        var queues = new List<Queue<T>>();
        var byHost = new Dictionary<string, Queue<T>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var key = host(item);
            if (!byHost.TryGetValue(key, out var queue))
            {
                queue = new Queue<T>();
                byHost[key] = queue;
                queues.Add(queue);
            }

            queue.Enqueue(item);
        }

        var result = new List<T>();
        while (queues.Count > 0)
        {
            for (var index = 0; index < queues.Count; index++)
            {
                result.Add(queues[index].Dequeue());
            }

            queues.RemoveAll(queue => queue.Count == 0);
        }

        return result;
    }
}
