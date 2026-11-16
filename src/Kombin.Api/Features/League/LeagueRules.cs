namespace Kombin.Api.Features.League;

/// <summary>The league's small pure rules (unit-tested in <c>LeagueRulesTests</c>).</summary>
internal static class LeagueRules
{
    /// <summary>
    /// Whether a birth year keeps the player out (rules: "doğum yılı 18'den küçük gösteren hesap katılamaz"), on the strict side: a known year
    /// blocks unless the player is certainly of age, i.e. <c>year − birthYear &lt;= minAge</c> in the current Istanbul year. A year alone cannot
    /// say whether the birthday has passed, so someone born in 2008 waits until 2027 even if they turned 18 in 2026 (the accepted cost). An
    /// unknown year is not checked: the consent sentence is the declaration. <paramref name="minAge"/> 0 checks nothing.
    /// </summary>
    public static bool IsUnderAge(int? birthYear, DateOnly today, int minAge) =>
        minAge > 0 && birthYear is { } year && today.Year - year <= minAge;
}
