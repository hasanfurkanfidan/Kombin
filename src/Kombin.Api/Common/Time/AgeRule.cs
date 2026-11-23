namespace Kombin.Api.Common.Time;

/// <summary>The minimum-age rule shared by the league and "Arkadaşına sor" (pure).</summary>
internal static class AgeRule
{
    /// <summary>
    /// Whether a birth year keeps someone out, on the strict side: a known year blocks unless the person is certainly of age, i.e.
    /// <c>year − birthYear &lt;= minAge</c> in the current Istanbul year. A year alone cannot say whether the birthday has passed, so someone born in
    /// 2008 waits until 2027 even if they turned 18 in 2026 (the accepted cost). An unknown year is not checked: the consent sentence is the
    /// declaration. <paramref name="minAge"/> 0 checks nothing.
    /// </summary>
    public static bool IsUnderAge(int? birthYear, DateOnly today, int minAge) =>
        minAge > 0 && birthYear is { } year && today.Year - year <= minAge;
}
