using System.Security.Cryptography;
using System.Text;

namespace Kombin.Api.Features.League;

/// <summary>One published version of the league consent text.</summary>
internal sealed record LeagueConsentVersion(string Version, string Text)
{
    /// <summary>SHA-256 of the text: <c>LeagueConsentTests</c> pins it per version, so a published text cannot be edited in place.</summary>
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}

/// <summary>
/// The consent asked once, on the first visit to Kombin Ligi ("Lige katıl"): the outfit is shown to strangers. Its last sentence is the 18+
/// declaration (rules, section 1). Versions are append-only like <c>PostingConsent</c>: to change the text, add a version at the end; every
/// player then accepts again. Version 2 (task L3-fix) adds the plain truth about the image copies: someone who knows the pieces can tell whose
/// outfit it is (the copies carry no name, profile or stable identifier, but they are the same pictures). Counsel has reviewed neither version
/// (<c>docs/privacy-notes.md</c>, open questions). Pure.
/// </summary>
internal static class LeagueConsent
{
    public static readonly IReadOnlyList<LeagueConsentVersion> Versions =
    [
        Published(
            "2026-11-v1",
            "Katılırsan dolabındaki parçaların fotoğrafları diğer oyunculara gösterilir. Adın ve profilin görünmez. İstediğin gün kombinini çıkarabilir, "
            + "ayarlardan ligden ayrılabilirsin. Katılarak 18 yaşından büyük olduğunu onaylarsın."),
        Published(
            "2026-11-v2",
            "Katılırsan dolabındaki parçaların fotoğrafları diğer oyunculara gösterilir. Adın ve profilin görünmez. Parçalarını tanıyan biri kombinin "
            + "sana ait olduğunu anlayabilir. İstediğin gün kombinini çıkarabilir, ayarlardan ligden ayrılabilirsin. Katılarak 18 yaşından büyük "
            + "olduğunu onaylarsın."),
    ];

    public static LeagueConsentVersion Current => Versions[^1];

    private static LeagueConsentVersion Published(string version, string text) => new(version, text.ReplaceLineEndings("\n"));
}
