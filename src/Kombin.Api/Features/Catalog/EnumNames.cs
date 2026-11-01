using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog;

/// <summary>Strict, case-insensitive enum parsing for ingestion payloads, plus the string form of <see cref="Season"/> flags.</summary>
internal static class EnumNames
{
    /// <summary>Matches a single defined name only: numbers and comma-separated lists, which <see cref="Enum.TryParse{T}(string?, bool, out T)"/> accepts, are rejected.</summary>
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

    public static bool IsValid<TEnum>(string? value)
        where TEnum : struct, Enum =>
        TryParse<TEnum>(value, out _);

    /// <summary>Season names accepted in payloads; <see cref="Season.None"/> and <see cref="Season.AllYear"/> are not seasons.</summary>
    public static bool IsValidSeason(string? value) =>
        TryParse<Season>(value, out var season) && season is not (Season.None or Season.AllYear);

    public static Season ParseSeasons(IEnumerable<string>? values) =>
        values?.Aggregate(Season.None, (seasons, name) => seasons | Enum.Parse<Season>(name, ignoreCase: true)) ?? Season.None;

    public static string[] SeasonNames(Season seasons) =>
        [.. new[] { Season.Spring, Season.Summer, Season.Autumn, Season.Winter }
            .Where(season => seasons.HasFlag(season))
            .Select(season => season.ToString())];
}
