using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.AskFriend;

internal static class AskFriendErrors
{
    /// <summary><c>AskFriend:Enabled</c> is false: the routes exist but the feature does not, yet.</summary>
    public static readonly Error Disabled =
        Error.NotFound("AskFriend.Disabled", "Arkadaşına sor is not open yet.");

    /// <summary>Also the answer for someone else's ask: never reveal that it exists.</summary>
    public static readonly Error NotFound =
        Error.NotFound("AskFriend.NotFound", "Ask not found.");

    public static readonly Error OutfitNotFound =
        Error.NotFound("AskFriend.OutfitNotFound", "Outfit not found.");

    public static readonly Error ConsentRequired =
        Error.Conflict("AskFriend.ConsentRequired", $"Accept the current consent ({AskFriendConsent.Current.Version}) first.");

    public static readonly Error AgeRestricted =
        Error.Forbidden("AskFriend.AgeRestricted", "Arkadaşına sor is for people aged 18 and over.");

    /// <summary>No piece of the outfit (or of one of the two) can be shown to a friend.</summary>
    public static readonly Error NothingToShow =
        Error.Conflict("AskFriend.NothingToShow", "None of the pieces of this outfit can be shown.");

    public static readonly Error DailyLimit =
        Error.Conflict("AskFriend.DailyLimit", "You have reached today's limit of questions; try again tomorrow.");

    /// <summary>The picture copies could not be stored: nothing was created, try again.</summary>
    public static readonly Error StorageUnavailable =
        Error.Unavailable("AskFriend.StorageUnavailable", "The pictures could not be prepared right now; try again in a minute.");
}
