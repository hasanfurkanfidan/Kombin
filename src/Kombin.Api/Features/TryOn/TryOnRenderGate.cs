using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.TryOn;

internal enum RenderGateVerdict
{
    /// <summary>The caller may render; it must call <see cref="TryOnRenderGate.Finish"/> afterwards.</summary>
    Started,

    /// <summary>One of the user's renders is running: answer <c>Preparing</c>.</summary>
    InFlight,

    /// <summary><c>TryOn:RendersPerHour</c> renders started in the last hour: 429.</summary>
    OverLimit,
}

internal sealed record RenderGateResult(RenderGateVerdict Verdict, TimeSpan RetryAfter);

/// <summary>
/// The brief's two render limits: <c>TryOn:RendersPerHour</c> new renders per user per rolling hour, and one render per user in flight. Every
/// call to the renderer counts (a <c>422</c> too); a cache hit never reaches the gate. In memory, <b>per instance</b>, like the rate limiter: a
/// second API instance would double both (<c>docs/design/try-on.md</c>, open questions).
/// </summary>
internal sealed class TryOnRenderGate(IOptionsMonitor<TryOnOptions> options)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Above this many users the expired ones are swept on the next start.</summary>
    private const int SweepAbove = 10_000;

    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, UserRenders> _users = [];

    public RenderGateResult TryStart(Guid userId, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_users.Count > SweepAbove)
            {
                Sweep(now);
            }

            if (!_users.TryGetValue(userId, out var user))
            {
                user = new UserRenders();
                _users[userId] = user;
            }

            Expire(user, now);

            if (user.InFlight)
            {
                return new RenderGateResult(RenderGateVerdict.InFlight, TimeSpan.Zero);
            }

            if (user.Starts.Count >= options.CurrentValue.RendersPerHour)
            {
                return new RenderGateResult(RenderGateVerdict.OverLimit, user.Starts.Peek() + Window - now);
            }

            user.InFlight = true;
            user.Starts.Enqueue(now);

            return new RenderGateResult(RenderGateVerdict.Started, TimeSpan.Zero);
        }
    }

    public void Finish(Guid userId)
    {
        lock (_sync)
        {
            if (_users.TryGetValue(userId, out var user))
            {
                user.InFlight = false;
            }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (userId, user) in _users.ToList())
        {
            Expire(user, now);
            if (!user.InFlight && user.Starts.Count == 0)
            {
                _users.Remove(userId);
            }
        }
    }

    private static void Expire(UserRenders user, DateTimeOffset now)
    {
        while (user.Starts.Count > 0 && user.Starts.Peek() + Window <= now)
        {
            user.Starts.Dequeue();
        }
    }

    private sealed class UserRenders
    {
        public bool InFlight { get; set; }

        public Queue<DateTimeOffset> Starts { get; } = new();
    }
}
