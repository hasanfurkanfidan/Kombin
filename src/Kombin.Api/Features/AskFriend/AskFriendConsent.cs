using System.Security.Cryptography;
using System.Text;

namespace Kombin.Api.Features.AskFriend;

/// <summary>One published version of the ask consent text.</summary>
internal sealed record AskFriendConsentVersion(string Version, string Text)
{
    /// <summary>SHA-256 of the text: <c>AskFriendConsentTests</c> pins it per version, so a published text cannot be edited in place.</summary>
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}

/// <summary>
/// The consent accepted with the first ask under a version ("Arkadaşına sor", design section 3): the pieces are shown to anyone with the link. It names
/// the 24-hour window, so changing <c>AskFriend:AnswerWindow</c> needs a new version. Versions are append-only like <c>LeagueConsent</c>. Pure.
/// </summary>
internal static class AskFriendConsent
{
    public static readonly IReadOnlyList<AskFriendConsentVersion> Versions =
    [
        Published(
            "2026-11-v1",
            "Linki alan herkes, giriş yapmadan bu kombinlerdeki parçaların fotoğraflarını görebilir ve cevap verebilir. Adın ve profilin sayfada "
            + "görünmez. Link 24 saat sonra kendiliğinden kapanır; istersen daha önce kapatabilirsin. Link kapanınca fotoğraflar silinir. Linki "
            + "paylaşarak 18 yaşından büyük olduğunu onaylarsın."),
    ];

    public static AskFriendConsentVersion Current => Versions[^1];

    private static AskFriendConsentVersion Published(string version, string text) => new(version, text.ReplaceLineEndings("\n"));
}
