using System.Security.Cryptography;
using System.Text;

namespace Kombin.Api.Features.Social;

/// <summary>One published version of the posting consent text.</summary>
internal sealed record ConsentVersion(string Version, string Text)
{
    /// <summary>SHA-256 of the text: <c>PostingConsentTests</c> pins it per version, so a published text cannot be edited in place.</summary>
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}

/// <summary>
/// The consent a user gives before their first post (photos of people are personal data). Versions are append-only: to change the text,
/// add a new version at the end and make it <see cref="Current"/>; every user then accepts again before posting. Pure.
/// </summary>
internal static class PostingConsent
{
    public static readonly IReadOnlyList<ConsentVersion> Versions =
    [
        Published(
            "2026-11-v1",
            """
            Paylaşım onayı

            Kombin'de paylaştığın fotoğraflar seni veya başkalarını gösterebilir; bu kişisel veridir. Paylaşmadan önce:

            1. Fotoğrafta görünen herkesin paylaşıma izni olduğunu onaylıyorsun.
            2. Gönderilerin sen "Takipçilerime" veya "Herkese açık" seçene kadar yalnızca sana görünür.
            3. Fotoğrafın konum ve cihaz bilgileri (EXIF) yüklemede silinir; orijinal dosya gizli saklanır, akışta yeniden işlenmiş bir kopyası gösterilir.
            4. Her fotoğraf yayından önce otomatik bir güvenlik kontrolünden geçer; şüpheli olanlar bir kişi tarafından incelenir.
            5. Fotoğraftaki kıyafetleri etiketlemek için otomatik öneriler alırsın; senin onaylamadığın hiçbir etiket başkalarına gösterilmez.
            6. Bir gönderiyi istediğin an silebilirsin; fotoğraf ve türetilmiş kopyaları en geç 24 saat içinde kalıcı olarak silinir.
            7. Kreatör gönderilerinden yapılan alışverişlerde Kombin ve kreatör komisyon kazanabilir; bu durum gönderide belirtilir.
            """),
    ];

    public static ConsentVersion Current => Versions[^1];

    /// <summary>Line endings are normalized so the text (and its pinned hash) does not depend on how the file was checked out.</summary>
    private static ConsentVersion Published(string version, string text) => new(version, text.ReplaceLineEndings("\n"));

    public static ConsentVersion? Find(string? version) => Versions.FirstOrDefault(candidate => candidate.Version == version);

    /// <summary>Whether the versions a user accepted include the current one (an older acceptance is not enough).</summary>
    public static bool IsSatisfied(IEnumerable<string> acceptedVersions) => acceptedVersions.Contains(Current.Version, StringComparer.Ordinal);
}
