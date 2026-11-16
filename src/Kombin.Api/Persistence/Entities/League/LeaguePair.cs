namespace Kombin.Api.Persistence.Entities.League;

/// <summary>
/// One pair served to a voter, and the vote on it (the same row: a vote exists only for a served pair). The ID is the only identifier the voter
/// sees. Unique per (voter, day, the two entries): a pair is never shown twice to the same player.
/// </summary>
internal sealed class LeaguePair
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public DateOnly Day { get; set; }

    public Guid VoterId { get; set; }

    public Guid LeftEntryId { get; set; }

    public Guid RightEntryId { get; set; }

    /// <summary>The smaller of the two entry IDs, for the uniqueness of the pair.</summary>
    public Guid LowEntryId { get; set; }

    /// <summary>The larger of the two entry IDs.</summary>
    public Guid HighEntryId { get; set; }

    public LeaguePairMatch MatchLevel { get; set; }

    /// <summary>1 to <c>League:MaxPairsPerDay</c> within the voter's day.</summary>
    public short Sequence { get; set; }

    public DateTimeOffset ServedAt { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    public LeaguePairChoice? Choice { get; set; }

    /// <summary>What the app measured from display to tap.</summary>
    public int? ClientDecisionMilliseconds { get; set; }

    public bool Counted { get; set; }

    /// <summary>1, or <c>League:NewVoterWeight</c> for the voter's first counted votes; 0 when not counted.</summary>
    public float Weight { get; set; }
}

/// <summary>One report per player and outfit. <see cref="Counts"/>: the reporter was trusted when reporting.</summary>
internal sealed class LeagueReport
{
    public Guid EntryId { get; set; }

    public Guid ReporterId { get; set; }

    public LeagueReportReason? Reason { get; set; }

    public bool Counts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
