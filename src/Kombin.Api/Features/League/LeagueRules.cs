namespace Kombin.Api.Features.League;

/// <summary>The league's small pure rules (unit-tested in <c>LeagueRulesTests</c>).</summary>
internal static class LeagueRules
{
    /// <summary>
    /// Whether a birth year shows the player under age (rules: "doğum yılı 18'den küçük gösteren hesap katılamaz"): <c>year − birthYear &lt;
    /// minAge</c> in the current Istanbul year. A year alone cannot say whether the birthday has passed, so someone born in 2008 may join in 2026;
    /// the consent sentence is their declaration, as it is for an unknown year. <paramref name="minAge"/> 0 checks nothing.
    /// </summary>
    public static bool IsUnderAge(int? birthYear, DateOnly today, int minAge) =>
        minAge > 0 && birthYear is { } year && today.Year - year < minAge;
}
