using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League;

/// <summary>
/// The Turkish lines the league writes itself (the rest is codes the app renders). Quoted from the rules; scanned by <c>CopyGlossaryTests</c>.
/// </summary>
internal static class LeagueCopy
{
    public const string RecentlyCompeted = "Bu kombin yakın zamanda yarıştı, bugün yarışmıyor.";

    /// <summary>The results of an outfit that was reported and is waiting for a review (owner's text, task L5).</summary>
    public const string UnderReview = "Kombinin inceleniyor. Sonuç inceleme bitince görünecek.";

    /// <summary>The line the owner sees for a reason, or null when the app renders the reason itself.</summary>
    public static string? For(LeagueEntryReason? reason) => reason == LeagueEntryReason.RecentlyCompeted ? RecentlyCompeted : null;
}
