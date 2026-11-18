using Kombin.Api.Features.League.GetResults;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League;

/// <summary>
/// Every Turkish line the league writes itself, in one place (task B8: the app must never invent a sentence where the API returns none). The
/// text follows the app glossary and says what the player can do next where there is something to do; <c>CopyGlossaryTests</c> scans this file and
/// <c>LeagueCopyTests</c> pins every line. A line that is the owner's wording (<see cref="RecentlyCompeted"/>) is quoted from the rules note.
/// </summary>
internal static class LeagueCopy
{
    /// <summary>The owner's line for the repeat rule (the rules note, section "Aynı kombin tekrar yarışamaz").</summary>
    public const string RecentlyCompeted = "Bu kombin yakın zamanda yarıştı, bugün yarışmıyor.";

    /// <summary>A piece was held back by the safety check. The owner is never told which piece (the wardrobe never shows verdicts either).</summary>
    public const string UnsafePiece = "Kombinindeki bir parçanın fotoğrafı yarışma için uygun bulunmadı, bu yüzden bugün yarışmıyor. Başka bir kombin giyip işaretleyebilirsin.";

    /// <summary>A piece is not from the wardrobe, or there are too few pieces (the number is a setting, so the line gives none).</summary>
    public const string IncompleteOutfit = "Bu kombinde yeterince parça yok ya da bazı parçalar dolabında değil. Dolabındaki parçalarla bir kombin giyip işaretlersen yarışır.";

    public const string PieceUnavailable = "Kombinindeki bir parça dolabından çıkarılmış ya da fotoğrafı yok, bu yüzden bugün yarışmıyor. Parçayı geri ekleyebilir ya da başka bir kombin giyebilirsin.";

    public const string AgeRestricted = "Kombin Ligi 18 yaş ve üzeri için. Doğum yılına göre bugün yarışamıyorsun.";

    public const string NotWorn = "Bu kombin artık giydim olarak işaretli değil, bu yüzden bugün yarışmıyor. Bugün giydiğin bir kombini işaretlersen yarışır.";

    /// <summary>
    /// A review took the outfit out. Only the day's vote points are zeroed (<c>LeagueEntryReviewer</c>), so the line says the other points stay.
    /// </summary>
    public const string Removed = "Kombinin incelendi ve yarıştan çıkarıldı, bu yüzden sıralaman ve oy puanın yok. Diğer puanların duruyor.";

    /// <summary>The results of an outfit that was reported and is waiting for a review (owner's text, task L5).</summary>
    public const string UnderReview = "Kombinin inceleniyor. Sonuç inceleme bitince görünecek.";

    /// <summary>The line the owner sees for a reason, or null for no reason.</summary>
    public static string? For(LeagueEntryReason? reason) => reason switch
    {
        LeagueEntryReason.RecentlyCompeted => RecentlyCompeted,
        LeagueEntryReason.UnsafePiece => UnsafePiece,
        LeagueEntryReason.IncompleteOutfit => IncompleteOutfit,
        LeagueEntryReason.PieceUnavailable => PieceUnavailable,
        LeagueEntryReason.AgeRestricted => AgeRestricted,
        LeagueEntryReason.NotWorn => NotWorn,
        _ => null,
    };

    /// <summary>The line for today's entry: its reason's while it does not compete, the removal's when a review took it out, else null.</summary>
    public static string? ForEntry(LeagueEntryStatus status, LeagueEntryReason? reason) => status switch
    {
        LeagueEntryStatus.NotCompeting => For(reason),
        LeagueEntryStatus.Removed => Removed,
        _ => null,
    };

    /// <summary>The line for a closed day's result: the review's, else null.</summary>
    public static string? ForResult(LeagueResultStatus status) => status switch
    {
        LeagueResultStatus.UnderReview => UnderReview,
        LeagueResultStatus.Removed => Removed,
        _ => null,
    };
}
