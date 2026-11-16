namespace Kombin.Api.Features.League.Rules;

/// <summary>The chosen outfit's record today, as the vote response shows it: never an estimate, never stored, never used for ranking.</summary>
internal sealed record ChosenRecord(int? WinRate, int? Games);

/// <summary>
/// "Her oydan sonra seçilen kombinin o günkü gerçek galibiyet oranı görünür" (design note section 7). Pure. The counters already include the
/// vote just given (read after the update). <c>Games</c> = the weighted games rounded down; <c>WinRate</c> = round(100 × wins / games), 0-100,
/// null below <c>League:MinGamesForWinRate</c> weighted games ("İlk oylardan biri seninki"). A skip chose nothing: both are null.
/// </summary>
internal static class WinRate
{
    public static ChosenRecord For(double weightedWins, double weightedGames, bool skipped, int minGames)
    {
        if (skipped)
        {
            return new ChosenRecord(null, null);
        }

        var games = (int)Math.Floor(weightedGames);
        if (weightedGames < minGames || weightedGames <= 0)
        {
            return new ChosenRecord(null, games);
        }

        var rate = (int)Math.Round(100 * weightedWins / weightedGames, MidpointRounding.AwayFromZero);

        return new ChosenRecord(Math.Clamp(rate, 0, 100), games);
    }
}
