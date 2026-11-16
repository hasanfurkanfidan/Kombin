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
}
