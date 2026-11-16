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
}
