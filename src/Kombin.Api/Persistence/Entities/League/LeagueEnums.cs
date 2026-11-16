namespace Kombin.Api.Persistence.Entities.League;

/// <summary>The five leagues, lowest first: Bronz, Gümüş, Altın, Platin, Elmas. Everyone starts in Bronze.</summary>
internal enum LeagueTier
{
    Bronze,
    Silver,
    Gold,
    Platinum,
    Diamond,
}

internal enum LeagueConsentAction
{
    Join,
    Leave,
}

internal enum LeagueEntryKind
{
    /// <summary>A player's worn outfit: ranked and listed.</summary>
    Human,

    /// <summary>A catalog-only outfit from the engine that fills a small pool: paired, never ranked or listed.</summary>
    Engine,
}

internal enum LeagueEntryStatus
{
    /// <summary>Waiting for the image safety check of a piece.</summary>
    Pending,
    Competing,
    NotCompeting,

    /// <summary>The player took it out (final for the day).</summary>
    Withdrawn,

    /// <summary>Taken out by a review after reports.</summary>
    Removed,
}

/// <summary>Why an entry does not compete today; the owner sees it.</summary>
internal enum LeagueEntryReason
{
    RecentlyCompeted,
    UnsafePiece,
    IncompleteOutfit,
    PieceUnavailable,
    AgeRestricted,
}

/// <summary>How strictly a pair matched: same occasion and audience, same audience only, or anything.</summary>
internal enum LeaguePairMatch
{
    Strict,
    SameAudience,
    Any,
}

internal enum LeaguePairChoice
{
    Left,
    Right,

    /// <summary>"Karar veremedim": never counted.</summary>
    Skip,
}

internal enum LeagueReportReason
{
    Inappropriate,
    NotClothing,
    Other,
}

internal enum LeagueWeekOutcome
{
    Stayed,
    Promoted,
    Relegated,

    /// <summary>Down one tier after two weeks in a row without a competed day.</summary>
    IdleRelegated,
}
