using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Persistence.Entities.League;

/// <summary>A player's points for one closed day (at most 100). Stored, never recomputed: a closed day does not change later.</summary>
internal sealed class LeagueDailyScore
{
    public Guid UserId { get; set; }

    public DateOnly Day { get; set; }

    /// <summary>Null when the player had no entry that day.</summary>
    public Guid? EntryId { get; set; }

    /// <summary>The entry was competing at the close (ranked or not).</summary>
    public bool Competed { get; set; }

    public bool Ranked { get; set; }

    public int? Rank { get; set; }

    public int RankedCount { get; set; }

    public int Comparisons { get; set; }

    public double? Percentile { get; set; }

    /// <summary>0-70.</summary>
    public int VotePoints { get; set; }

    /// <summary>0 or 15.</summary>
    public int VotingPoints { get; set; }

    /// <summary>0 or 10.</summary>
    public int WornPoints { get; set; }

    /// <summary>0 or 5.</summary>
    public int PiecePoints { get; set; }

    /// <summary>0-100.</summary>
    public int Total { get; set; }

    /// <summary>Set by the Notification feature's result push.</summary>
    public DateTimeOffset? NotifiedAt { get; set; }

    /// <summary>
    /// The entry was hidden by reports and not yet reviewed at the close: it is ranked and its points are stored (they count in the week), but its
    /// owner sees "under review" without the rank or the vote points until a reviewer restores or removes it (<c>LeagueEntryReviewer</c>).
    /// </summary>
    public bool UnderReview { get; set; }

    /// <summary>A reviewer removed the entry after the close: its vote points became 0 and its rank is gone; other outfits keep theirs.</summary>
    public bool RemovedByReview { get; set; }
}

/// <summary>A week, Monday 00:00 to Sunday 21:00 Istanbul.</summary>
internal sealed class LeagueWeek
{
    /// <summary>A Monday.</summary>
    public DateOnly WeekStart { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset? GroupsFormedAt { get; set; }
}

/// <summary>About 30 players of one tier (or of neighbouring tiers merged when a tier is small) for one week.</summary>
internal sealed class LeagueGroup
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public DateOnly WeekStart { get; set; }

    /// <summary>One tier, or several when merged; every member keeps their own label.</summary>
    public LeagueTier[] Tiers { get; set; } = [];

    public int Capacity { get; set; }
}

/// <summary>A player's place in a week's group. The alias is the only label other players see.</summary>
internal sealed class LeagueGroupMember
{
    public const int MaxAliasLength = 40;

    public DateOnly WeekStart { get; set; }

    public Guid UserId { get; set; }

    public Guid GroupId { get; set; }

    /// <summary>"Yeşil Trençkot": generated for the week, unique in the group, never derived from the user.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>The player's own tier for the week.</summary>
    public LeagueTier Tier { get; set; }

    public int? WeekPoints { get; set; }

    public short? CompetedDays { get; set; }

    public int? FinalRank { get; set; }

    public LeagueWeekOutcome? Outcome { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>The background style score and the league part of creator eligibility. No endpoint and no screen read it.</summary>
internal sealed class LeagueStyleScore
{
    public const int MaxRevokedReasonLength = 200;

    public Guid UserId { get; set; }

    public DateTimeOffset ComputedAt { get; set; }

    public int CountedDays { get; set; }

    public double? MeanPercentile { get; set; }

    public double? SdPercentile { get; set; }

    /// <summary>0-100; null below two counted days.</summary>
    public double? Score { get; set; }

    public short QualifyingWeeks { get; set; }

    /// <summary>Set once the league part was met; never cleared by the computation.</summary>
    public DateTimeOffset? LeagueEligibleAt { get; set; }

    /// <summary>Reserved: v1 never revokes and writes this nowhere.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Reserved: v1 never revokes and writes this nowhere.</summary>
    public string? RevokedReason { get; set; }
}

/// <summary>A counted vote that involved an engine outfit, kept for the engine with no user ID at all.</summary>
internal sealed class LeagueEnginePreference
{
    public long Id { get; set; }

    public DateOnly Day { get; set; }

    public Audience Audience { get; set; }

    public Occasion? Occasion { get; set; }

    /// <summary>The engine outfit's products when it won; empty when the human outfit won (a player's pieces are never kept here).</summary>
    public Guid[] WinnerProducts { get; set; } = [];

    /// <summary>The engine outfit's products when it lost; empty when it won.</summary>
    public Guid[] LoserProducts { get; set; } = [];

    public float Weight { get; set; }
}

/// <summary>Key/value state of the league jobs, so no job keeps state in memory.</summary>
internal sealed class LeagueState
{
    public const int MaxKeyLength = 100;

    public const int MaxValueLength = 1000;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
