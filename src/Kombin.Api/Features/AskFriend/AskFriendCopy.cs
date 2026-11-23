using Kombin.Api.Persistence.Entities.AskFriend;

namespace Kombin.Api.Features.AskFriend;

/// <summary>
/// The Turkish lines the server writes for "Arkadaşına sor" (the page's own copy is a template). <c>CopyGlossaryTests</c> scans this file.
/// </summary>
internal static class AskFriendCopy
{
    public const string PairShareText = "Bugün hangisini giyeyim? Bir dokunuşla cevap ver:";

    public const string SingleShareText = "Bu kombin olur mu? Bir dokunuşla cevap ver:";

    public const string HiddenStatusMessage = "Link bildirimler yüzünden kapatıldı.";

    /// <summary>The text that goes with the link in the share sheet.</summary>
    public static string ShareText(AskKind kind) => kind == AskKind.Pair ? PairShareText : SingleShareText;
}
