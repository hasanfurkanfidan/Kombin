namespace Kombin.Api.Features.League.Rules;

/// <summary>
/// A ranked outfit: its rank (1 = best; tied outfits share the rank of the first of them) and its percentile as an exact fraction
/// <c>Numerator / Denominator</c> (0-1, 1 the best), so the vote points are rounded from the fraction, not from a double.
/// </summary>
internal sealed record RankedOutfit(Guid EntryId, int Rank, long Numerator, long Denominator)
{
    public double Value => (double)Numerator / Denominator;
}

/// <summary>
/// The day's ranking and percentile (design note section 8, steps 3 and 4; the rules, section 3). Pure.
/// <list type="bullet">
/// <item>Order: strength descending, then entry ID (<see cref="Guid.CompareTo(Guid)"/>), a stable key, so equal inputs give an equal order.</item>
/// <item>Ties: outfits whose strengths are equal within <see cref="TieTolerance"/> on the log scale (measured from the first outfit of the
/// group, never chained) share a rank, <c>1 + the number strictly stronger</c>, and the average of their positions.</item>
/// <item>Percentile: the rules' <c>p = (sıra − 0,5) / N</c> with <c>sıra</c> counted from the worst (the only reading that makes "1 en iyi"
/// true); with <c>r</c> the (average) position from the top, <c>p = (N − r + 0,5) / N</c>. Kept as a fraction: with <c>s</c> the sum of the
/// group's first and last position, <c>p = (2N − s + 1) / (2N)</c>.</item>
/// </list>
/// </summary>
internal static class Percentile
{
    public const double TieTolerance = 1e-9;

    public static IReadOnlyList<RankedOutfit> Rank(IReadOnlyList<(Guid EntryId, double Strength)> outfits)
    {
        ArgumentNullException.ThrowIfNull(outfits);

        var sorted = outfits.OrderByDescending(outfit => outfit.Strength).ThenBy(outfit => outfit.EntryId).ToList();
        var n = sorted.Count;
        var ranked = new List<RankedOutfit>(n);

        for (var start = 0; start < n;)
        {
            var end = start;
            while (end + 1 < n && Math.Abs(Math.Log(sorted[start].Strength) - Math.Log(sorted[end + 1].Strength)) < TieTolerance)
            {
                end++;
            }

            // Positions are 1-based: the group holds positions start + 1 .. end + 1.
            long positions = (start + 1) + (end + 1);
            for (var index = start; index <= end; index++)
            {
                ranked.Add(new RankedOutfit(sorted[index].EntryId, start + 1, (2L * n) - positions + 1, 2L * n));
            }

            start = end + 1;
        }

        return ranked;
    }
}
