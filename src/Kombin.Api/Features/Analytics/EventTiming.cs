namespace Kombin.Api.Features.Analytics;

/// <summary>Phones have wrong clocks. An event time too far off is not trusted but does not cost the event: it takes the server's time.</summary>
internal static class EventTiming
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    /// <summary>The client's time, or <paramref name="receivedAt"/> when it is more than 7 days old or more than 5 minutes ahead. The bounds themselves are kept.</summary>
    public static DateTimeOffset Clamp(DateTimeOffset occurredAt, DateTimeOffset receivedAt) =>
        occurredAt < receivedAt - MaxAge || occurredAt > receivedAt + MaxFutureSkew
            ? receivedAt
            : occurredAt.ToUniversalTime();
}
