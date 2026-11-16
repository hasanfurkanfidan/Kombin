using Kombin.Api.Common.Time;

namespace Kombin.Api.Features.League;

/// <summary>A league day is a calendar day in Europe/Istanbul that closes at <c>League:DayClosesAt</c> (21:00). Pure.</summary>
internal static class LeagueDays
{
    /// <summary>Istanbul midnight of <paramref name="day"/>, as an instant.</summary>
    public static DateTimeOffset Start(DateOnly day) => At(day, TimeOnly.MinValue);

    /// <summary>When votes stop and the day's outfit is fixed: <paramref name="closesAt"/> Istanbul time of <paramref name="day"/>, as an instant.</summary>
    public static DateTimeOffset ClosesAt(DateOnly day, TimeOnly closesAt) => At(day, closesAt);

    private static DateTimeOffset At(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);

        return new DateTimeOffset(local, Istanbul.Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
