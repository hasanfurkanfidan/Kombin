using Kombin.Api.Persistence.Entities.AskFriend;

namespace Kombin.Api.Features.AskFriend;

/// <summary>What an ask is right now, for the asker's screens and the public page. Also the <c>ask_view</c> analytics <c>status</c>.</summary>
internal enum AskViewState
{
    Open,
    Expired,
    Closed,
    Hidden,
}

internal static class AskState
{
    /// <summary>
    /// Hidden and Closed win over Expired (the stored status is what the asker or the reports decided); an open ask is expired from its
    /// <c>ExpiresAt</c> on. Pure.
    /// </summary>
    public static AskViewState Of(Ask ask, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ask);

        return ask.Status switch
        {
            AskStatus.Hidden => AskViewState.Hidden,
            AskStatus.Closed => AskViewState.Closed,
            _ => now >= ask.ExpiresAt ? AskViewState.Expired : AskViewState.Open,
        };
    }
}
