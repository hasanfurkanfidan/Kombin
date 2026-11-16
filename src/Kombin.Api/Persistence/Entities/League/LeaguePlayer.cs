namespace Kombin.Api.Persistence.Entities.League;

/// <summary>
/// A user who joined Kombin Ligi at least once: the current state (tier, joined or left). The join and leave history, with the consent text that
/// was shown, is <see cref="LeagueConsentRecord"/>.
/// </summary>
internal sealed class LeaguePlayer : IAuditable
{
    public Guid UserId { get; set; }

    public LeagueTier Tier { get; set; } = LeagueTier.Bronze;

    /// <summary>The latest join.</summary>
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>Set by leaving, cleared by joining again (the tier is kept).</summary>
    public DateTimeOffset? LeftAt { get; set; }

    /// <summary>The consent version accepted at the latest join.</summary>
    public string ConsentVersion { get; set; } = string.Empty;

    /// <summary>Counted votes over the account's life; the first <c>League:NewVoterVotes</c> weigh half.</summary>
    public int CountedVotes { get; set; }

    /// <summary>Closed weeks in a row without a competed day; at <c>League:IdleWeeksToRelegate</c> the player goes down one tier.</summary>
    public short IdleWeeks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsJoined => LeftAt is null;
}

/// <summary>One join or leave, append-only. A join stores the exact consent text shown, as <c>social.posting_consents</c> does.</summary>
internal sealed class LeagueConsentRecord
{
    public const int MaxVersionLength = 40;

    public const int MaxTextLength = 4000;

    public long Id { get; set; }

    public Guid UserId { get; set; }

    public LeagueConsentAction Action { get; set; }

    public string? Version { get; set; }

    public string? Text { get; set; }

    public DateTimeOffset At { get; set; }
}
