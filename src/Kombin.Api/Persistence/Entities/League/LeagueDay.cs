namespace Kombin.Api.Persistence.Entities.League;

/// <summary>One league day (Europe/Istanbul) and its close. A day without a row is open and has closed nothing.</summary>
internal sealed class LeagueDay
{
    public DateOnly Day { get; set; }

    /// <summary>21:00 Istanbul of <see cref="Day"/> (<c>League:DayClosesAt</c>); votes stop here.</summary>
    public DateTimeOffset ClosesAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Human entries with enough comparisons: the N of the percentile.</summary>
    public int RankedCount { get; set; }

    public int EngineCount { get; set; }

    public int PairCount { get; set; }

    public int BtIterations { get; set; }

    public bool BtConverged { get; set; }

    public int CloseMilliseconds { get; set; }
}
