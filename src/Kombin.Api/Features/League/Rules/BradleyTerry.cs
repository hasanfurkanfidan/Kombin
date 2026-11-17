namespace Kombin.Api.Features.League.Rules;

/// <summary>One counted vote of the day: the outfit chosen, the one not chosen (indexes into the day's outfits) and the vote's weight.</summary>
internal readonly record struct BtGame(int Winner, int Loser, double Weight);

/// <summary>The fitted strengths (π, one per outfit, in the caller's order), the iterations run and whether the stopping rule was met.</summary>
internal sealed record BtFit(double[] Strengths, int Iterations, bool Converged);

/// <summary>
/// The day's Bradley–Terry fit (design note section 8; the rules, section 3: "her kombine ortalama bir rakibe karşı yarım galibiyet ve yarım
/// yenilgi sayılan sanal bir başlangıç"). Pure and deterministic.
/// <para>
/// The model: P(i beats j) = π_i / (π_i + π_j), plus one fixed virtual opponent of strength 1 against which every outfit has <c>priorWins</c>
/// wins and <c>priorLosses</c> losses. The fit is the maximum of that likelihood, found with Newman's fixed-point iteration (M. E. J. Newman,
/// "Efficient computation of rankings from pairwise comparisons", JMLR 24, 2023), applied in place (Gauss–Seidel: outfit by outfit, in index
/// order, each update using the newest strengths):
/// <code>
/// π_i ← ( Σ_j w_ij π_j / (π_i + π_j)  +  priorWins / (π_i + 1) )  /  ( Σ_j w_ji / (π_i + π_j)  +  priorLosses / (π_i + 1) )
/// </code>
/// <c>w_ij</c> is the total weight of the votes in which i beat j. Its fixed point is that of the minorization–maximization step the design note
/// names (Hunter 2004), but MM converges so slowly that on simulated days of 650 and 6,100 outfits it had not met the stopping rule after 500
/// iterations; this one stops after about 160 and 75, and a pure chain of wins (where the same iteration done in one simultaneous step
/// oscillates for ever) after 25. Every π starts at 1. A sweep ends with <c>max_i |ln π_i(new) − ln π_i(old)| &lt; tolerance</c> (1e-6) or
/// the fit stops after <c>maxIterations</c> sweeps (500, reported as not converged). The virtual opponent anchors the scale (no normalisation
/// step), keeps an unbeaten outfit finite and puts parts of the pool that never met (two audiences) on one comparable scale.
/// </para>
/// <para>
/// Determinism: the votes are summed per pair of outfits in (lower index, higher index) order, the outfits are updated in index order (the
/// caller orders them by entry ID) and every sum runs in a fixed order, so the same votes give the same doubles to the last bit, whatever order
/// the votes were passed in.
/// </para>
/// </summary>
internal static class BradleyTerry
{
    public static BtFit Fit(int count, IReadOnlyList<BtGame> games, double priorWins, double priorLosses, double tolerance, int maxIterations)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priorWins);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priorLosses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);

        var strengths = new double[count];
        Array.Fill(strengths, 1.0);
        if (count == 0)
        {
            return new BtFit(strengths, 0, true);
        }

        foreach (var game in games)
        {
            if (game.Winner == game.Loser || (uint)game.Winner >= (uint)count || (uint)game.Loser >= (uint)count)
            {
                throw new ArgumentException($"A game names an outfit outside 0..{count - 1} or the same outfit twice.", nameof(games));
            }
        }

        var (offsets, opponents, won, lost) = Neighbours(count, games);

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var change = 0.0;
            for (var i = 0; i < count; i++)
            {
                var own = strengths[i];
                var numerator = priorWins / (own + 1.0);
                var denominator = priorLosses / (own + 1.0);
                for (var k = offsets[i]; k < offsets[i + 1]; k++)
                {
                    var other = strengths[opponents[k]];
                    var sum = own + other;
                    numerator += won[k] * other / sum;
                    denominator += lost[k] / sum;
                }

                var updated = numerator / denominator;
                change = Math.Max(change, Math.Abs(Math.Log(updated) - Math.Log(own)));
                strengths[i] = updated;
            }

            if (change < tolerance)
            {
                return new BtFit(strengths, iteration, true);
            }
        }

        return new BtFit(strengths, maxIterations, false);
    }

    /// <summary>
    /// Every outfit's opponents with the weight it won and lost against each (one entry per pair of outfits that met), as compressed rows:
    /// outfit i's opponents are <c>opponents[offsets[i] .. offsets[i + 1])</c>, in increasing index order.
    /// </summary>
    private static (int[] Offsets, int[] Opponents, double[] Won, double[] Lost) Neighbours(int count, IReadOnlyList<BtGame> games)
    {
        // Per pair of outfits (low, high): the weight of the low one's wins and of the high one's, summed in a fixed order.
        var ordered = games
            .Where(game => game.Weight > 0)
            .OrderBy(game => Math.Min(game.Winner, game.Loser))
            .ThenBy(game => Math.Max(game.Winner, game.Loser))
            .ThenBy(game => game.Winner)
            .ThenBy(game => game.Weight)
            .ToList();

        var pairs = new List<(int Low, int High, double LowWins, double HighWins)>();
        foreach (var game in ordered)
        {
            var (low, high) = (Math.Min(game.Winner, game.Loser), Math.Max(game.Winner, game.Loser));
            if (pairs.Count == 0 || pairs[^1].Low != low || pairs[^1].High != high)
            {
                pairs.Add((low, high, 0, 0));
            }

            var last = pairs[^1];
            pairs[^1] = game.Winner == low ? last with { LowWins = last.LowWins + game.Weight } : last with { HighWins = last.HighWins + game.Weight };
        }

        var offsets = new int[count + 1];
        foreach (var pair in pairs)
        {
            offsets[pair.Low + 1]++;
            offsets[pair.High + 1]++;
        }

        for (var i = 0; i < count; i++)
        {
            offsets[i + 1] += offsets[i];
        }

        var opponents = new int[offsets[count]];
        var won = new double[offsets[count]];
        var lost = new double[offsets[count]];
        var cursor = offsets[..count];

        // Opponents of i with a lower index come from pairs (j, i), those with a higher one from pairs (i, j); a stable two-pass fill keeps each
        // row in increasing opponent order.
        foreach (var pair in pairs.OrderBy(pair => pair.High).ThenBy(pair => pair.Low))
        {
            var slot = cursor[pair.High]++;
            (opponents[slot], won[slot], lost[slot]) = (pair.Low, pair.HighWins, pair.LowWins);
        }

        foreach (var pair in pairs)
        {
            var slot = cursor[pair.Low]++;
            (opponents[slot], won[slot], lost[slot]) = (pair.High, pair.LowWins, pair.HighWins);
        }

        return (offsets, opponents, won, lost);
    }
}
