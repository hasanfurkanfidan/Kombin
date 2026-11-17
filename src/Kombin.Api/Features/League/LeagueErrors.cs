using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.League;

internal static class LeagueErrors
{
    /// <summary><c>League:Enabled</c> is false: the routes exist but the league does not, yet.</summary>
    public static readonly Error Disabled =
        Error.NotFound("League.Disabled", "Kombin Ligi is not open yet.");

    public static readonly Error ConsentVersionInvalid =
        Error.Validation("League.ConsentVersionInvalid", $"Only the current league consent ({LeagueConsent.Current.Version}) can be accepted.");

    public static readonly Error AgeRestricted =
        Error.Forbidden("League.AgeRestricted", "Kombin Ligi is for players aged 18 and over.");

    public static readonly Error NotJoined =
        Error.Forbidden("League.NotJoined", "Join Kombin Ligi first.");

    public static readonly Error NoEntryToday =
        Error.NotFound("League.NoEntryToday", "There is no outfit of yours in today's league.");

    /// <summary>Votes stop at the day's close (21:00 Istanbul); the day's outfit is fixed from then on.</summary>
    public static readonly Error DayClosed =
        Error.Conflict("League.DayClosed", "Today's league is closed; results come at the close.");

    /// <summary>Also the answer for someone else's pair: never reveal that it exists.</summary>
    public static readonly Error PairNotFound =
        Error.NotFound("League.PairNotFound", "Pair not found.");

    public static readonly Error AlreadyVoted =
        Error.Conflict("League.AlreadyVoted", "This pair already has a different vote.");

    public static readonly Error AlreadyReported =
        Error.Conflict("League.AlreadyReported", "You already reported this outfit.");

    /// <summary>The caller is in no group this week (joined after Sunday's close: their group starts on Monday).</summary>
    public static readonly Error NoGroup =
        Error.NotFound("League.NoGroup", "You are not in a league group this week yet.");

    /// <summary>No such player outfit (engine outfits are never reviewed).</summary>
    public static readonly Error EntryNotFound =
        Error.NotFound("League.EntryNotFound", "League entry not found.");

    /// <summary>A removed outfit cannot be restored: removal is final.</summary>
    public static readonly Error NotReviewable =
        Error.Conflict("League.NotReviewable", "This outfit was removed; a removal is final.");

    /// <summary>The day is not closed yet, or the caller has no result on it (no entry and no points).</summary>
    public static readonly Error ResultsNotReady =
        Error.NotFound("League.ResultsNotReady", "There are no results for you on this day.");

    public static readonly Error InvalidDay =
        Error.Validation("League.InvalidDay", "The day must be a date in the form yyyy-MM-dd.");

    /// <summary>Another close holds the league's close lock (the hourly job, or another call).</summary>
    public static readonly Error CloseInProgress =
        Error.Conflict("League.CloseInProgress", "A league day is being closed right now; try again in a minute.");

    /// <summary>Votes on the day are still open: its close time has not come.</summary>
    public static readonly Error DayNotOver =
        Error.Conflict("League.DayNotOver", "This league day has not reached its close time yet.");
}
