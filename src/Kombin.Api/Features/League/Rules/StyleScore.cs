namespace Kombin.Api.Features.League.Rules;

/// <summary>The style score of one player: the counted days, the mean and sample deviation of their percentiles, and the score (null below two days).</summary>
internal sealed record StyleResult(int CountedDays, double? Mean, double? Deviation, double? Score);

/// <summary>
/// The style score and the league part of creator eligibility (design note section 11; the rules, section 6). Pure. No endpoint and no screen
/// read it.
/// <list type="bullet">
/// <item><b>Days</b>: the player's ranked days of the last <c>StyleScore:WindowDays</c> (90) on which at least <c>StyleScore:MinRankedPerDay</c>
/// (30) human outfits were ranked; the caller picks them. Usage points never enter.</item>
/// <item><b>Score</b>: <c>100 × (mean(p) − sd(p) / √n)</c>, <c>sd</c> the sample deviation (n − 1), clamped to 0-100; null when n &lt; 2.</item>
/// <item><b>A qualifying week</b>: <c>n ≥ StyleScore:MinDays</c> (30) and score ≥ <c>StyleScore:Threshold</c> (75); <c>qualifying_weeks</c> counts
/// them in a row and restarts at 0 on a miss.</item>
/// <item><b>Eligible</b>: at <c>StyleScore:ConsecutiveWeeks</c> (4) with no entry of the player removed by a review in the last 90 days;
/// <c>league_eligible_at</c> is then set once and never cleared by the computation ("Kazanılan hak geri alınmaz").</item>
/// </list>
/// </summary>
internal static class StyleScore
{
    public static StyleResult Compute(IReadOnlyList<double> percentiles)
    {
        ArgumentNullException.ThrowIfNull(percentiles);

        var n = percentiles.Count;
        if (n == 0)
        {
            return new StyleResult(0, null, null, null);
        }

        var mean = percentiles.Sum() / n;
        if (n < 2)
        {
            return new StyleResult(n, mean, null, null);
        }

        var deviation = Math.Sqrt(percentiles.Sum(p => (p - mean) * (p - mean)) / (n - 1));
        var score = Math.Clamp(100 * (mean - (deviation / Math.Sqrt(n))), 0, 100);

        return new StyleResult(n, mean, deviation, score);
    }

    public static bool Qualifies(StyleResult result, LeagueStyleScoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        return result.CountedDays >= options.MinDays && result.Score >= options.Threshold;
    }

    /// <summary>The qualifying weeks in a row after this week's check, and whether the league part of eligibility is met now.</summary>
    public static (short QualifyingWeeks, bool Eligible) Next(short qualifyingWeeksBefore, bool qualifies, bool removedRecently, int consecutiveWeeks)
    {
        var weeks = qualifies ? (short)Math.Min(short.MaxValue, qualifyingWeeksBefore + 1) : (short)0;

        return (weeks, weeks >= consecutiveWeeks && !removedRecently);
    }
}
