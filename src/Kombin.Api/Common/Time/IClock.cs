namespace Kombin.Api.Common.Time;

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
}
