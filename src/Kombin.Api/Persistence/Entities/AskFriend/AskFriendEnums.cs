namespace Kombin.Api.Persistence.Entities.AskFriend;

internal enum AskKind
{
    /// <summary>One outfit: "Bu kombin olur mu?", answered Yes or No.</summary>
    Single,

    /// <summary>Two outfits: "Hangisi?", answered A or B.</summary>
    Pair,
}

/// <summary>
/// The stored status. An ask past its <c>expires_at</c> stays <see cref="Open"/> here; the feature answers it as expired.
/// </summary>
internal enum AskStatus
{
    Open,

    /// <summary>Closed by the asker.</summary>
    Closed,

    /// <summary>Hidden after enough reports.</summary>
    Hidden,
}

internal enum AskOption
{
    A,
    B,
}

internal enum AskChoice
{
    Yes,
    No,
    A,
    B,
}

internal enum AskReportReason
{
    Nudity,
    Harassment,
    Spam,
    Other,
}
