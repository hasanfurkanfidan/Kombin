namespace Kombin.Api.Persistence.Entities.League;

/// <summary>
/// A voter's own counters of one league day: pairs served (the daily limit and the next pair's sequence) and votes that counted (the voting
/// points). Kept apart from the pair rows on purpose: a pair is deleted with the account of the player whose outfit it showed, and the voter's
/// "voted on ten pairs" must not shrink because of that.
/// </summary>
internal sealed class LeagueVoterDay
{
    public Guid UserId { get; set; }

    public DateOnly Day { get; set; }

    /// <summary>Pairs served to the voter this day; the last pair's sequence.</summary>
    public int Served { get; set; }

    /// <summary>Counted votes this day (not skips, not too fast).</summary>
    public int Counted { get; set; }
}
