using Kombin.Api.Common.Time;

namespace Kombin.Api.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
