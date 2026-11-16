using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League;

/// <summary>The words the league's requests take (<c>left</c>, <c>right</c>, <c>skip</c>, report reasons): defined names only, any casing. Pure.</summary>
internal static class LeagueChoices
{
    /// <summary>A single defined name: numbers and comma-separated lists, which <see cref="Enum.TryParse{T}(string?, bool, out T)"/> accepts, are refused.</summary>
    public static bool TryParse<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }

    public static bool IsChoice(string? value) => TryParse<LeaguePairChoice>(value, out _);

    /// <summary>A side of a pair: <c>left</c> or <c>right</c>.</summary>
    public static bool IsSide(string? value) => TryParse<LeaguePairChoice>(value, out var side) && side != LeaguePairChoice.Skip;

    /// <summary>Only after validation (<see cref="IsChoice"/>).</summary>
    public static LeaguePairChoice Parse(string value) =>
        TryParse<LeaguePairChoice>(value, out var choice) ? choice : throw new ArgumentException($"'{value}' is not a pair choice.", nameof(value));
}
