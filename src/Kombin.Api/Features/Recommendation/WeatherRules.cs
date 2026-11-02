using Kombin.Api.Features.Recommendation.Weather;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

// WEATHER RULES - what the day's weather asks of an outfit. Temperatures are the day's "dressing temperature", (2·max + min) / 3,
// weighted towards the maximum because you dress for the hours you are out, not for 5 a.m. Without a forecast (no city, the
// provider off or down) the season of the date stands in for the band.

internal enum TemperatureBand
{
    Hot,
    Warm,
    Mild,
    Cool,
    Cold,
}

internal enum OuterwearPolicy
{
    /// <summary>No layer: it would only be carried around.</summary>
    Forbidden,
    Optional,

    /// <summary>Every outfit has a layer; when the wardrobe has none, the catalog may fill it.</summary>
    Required,
}

/// <summary>A band: from <c>MinC</c> (inclusive) up to the next band's minimum.</summary>
internal sealed record BandRule(
    TemperatureBand Band,
    double MinC,
    OuterwearPolicy Outerwear,
    Season Seasons,
    IReadOnlySet<string> BannedLeaves,
    IReadOnlySet<string>? OuterwearLeaves);

/// <summary>What the engine applies for one day; built from a forecast or, without one, from the date's season.</summary>
internal sealed record WeatherConditions(
    TemperatureBand? Band,
    bool Rain,
    int? TemperatureC,
    Season Seasons,
    OuterwearPolicy Outerwear,
    IReadOnlySet<string> BannedLeaves,
    IReadOnlySet<string>? OuterwearLeaves);

internal static class WeatherRules
{
    /// <summary>
    /// Warmest first.
    /// <list type="bullet">
    /// <item>Hot (26 °C and up): no layer; knitwear and boots trap heat.</item>
    /// <item>Warm (19-25): a layer is optional and light, a jacket or a blazer for the evening; puffers and wool coats are too much.</item>
    /// <item>Mild (12-18): optional, any layer.</item>
    /// <item>Cool (5-11): a layer is required, and a blazer alone is not warm enough.</item>
    /// <item>Cold (below 5): a real coat is required, a puffer or a wool coat.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<BandRule> Bands =
    [
        new(TemperatureBand.Hot, 26, OuterwearPolicy.Forbidden, Season.Summer, Leaves("triko", "bot"), null),
        new(TemperatureBand.Warm, 19, OuterwearPolicy.Optional, Season.Spring | Season.Summer, Leaves(), Leaves("ceket", "blazer")),
        new(TemperatureBand.Mild, 12, OuterwearPolicy.Optional, Season.Spring | Season.Autumn, Leaves(), null),
        new(TemperatureBand.Cool, 5, OuterwearPolicy.Required, Season.Autumn | Season.Winter, Leaves(), Leaves("mont", "kaban", "ceket")),
        new(TemperatureBand.Cold, double.MinValue, OuterwearPolicy.Required, Season.Winter, Leaves(), Leaves("mont", "kaban")),
    ];

    /// <summary>
    /// Rain makes a layer required (except on a hot day, where an umbrella beats a coat) and bans open shoes (sandals and slippers;
    /// the catalog has none yet, the rule is ready for them).
    /// </summary>
    public static readonly IReadOnlySet<string> RainBannedLeaves = Leaves("sandalet", "terlik");

    /// <summary>
    /// In the rain boots are the right shoe, sneakers will do, and leather loafers soak through. Unlisted shoes score 0.8.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> RainShoeScores = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["bot"] = 1.0,
        ["sneaker"] = 0.8,
        ["loafer"] = 0.6,
    };

    /// <summary>White shoes and puddles: allowed, but marked down in the rain.</summary>
    public const double RainWhiteShoesFactor = 0.8;

    /// <summary>A piece sold for other seasons still works, it is just not the natural choice.</summary>
    public const double OffSeasonScore = 0.6;

    /// <summary>A forecast is rainy at a 50 % chance, from 1 mm, or when its weather code is drizzle, rain, snow or a storm.</summary>
    public const int RainProbabilityPercent = 50;

    public const double RainMillimetres = 1.0;

    public static double DressingTemperature(double minC, double maxC) => ((2 * maxC) + minC) / 3;

    public static BandRule BandFor(double temperatureC) => Bands.First(rule => temperatureC >= rule.MinC);

    /// <summary>WMO codes: 51-67 drizzle and rain, 71-77 snow, 80-86 showers, 95-99 thunderstorms.</summary>
    public static bool IsRainy(WeatherSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.PrecipitationProbability >= RainProbabilityPercent
            || snapshot.PrecipitationMm >= RainMillimetres
            || snapshot.WeatherCode is >= 51 and <= 67 or >= 71 and <= 77 or >= 80 and <= 86 or >= 95 and <= 99;
    }

    /// <summary>Northern-hemisphere meteorological seasons, as in Istanbul.</summary>
    public static Season SeasonOf(DateOnly date) => date.Month switch
    {
        3 or 4 or 5 => Season.Spring,
        6 or 7 or 8 => Season.Summer,
        9 or 10 or 11 => Season.Autumn,
        _ => Season.Winter,
    };

    public static WeatherConditions From(WeatherSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var temperature = DressingTemperature(snapshot.TempMinC, snapshot.TempMaxC);
        var band = BandFor(temperature);
        var rain = IsRainy(snapshot);

        var outerwear = rain && band.Band != TemperatureBand.Hot ? OuterwearPolicy.Required : band.Outerwear;
        var banned = rain ? new HashSet<string>(band.BannedLeaves.Concat(RainBannedLeaves), StringComparer.Ordinal) : band.BannedLeaves;

        return new WeatherConditions(band.Band, rain, (int)Math.Round(temperature, MidpointRounding.AwayFromZero), band.Seasons, outerwear, banned, band.OuterwearLeaves);
    }

    /// <summary>No forecast: the season decides. Winter asks for a layer, summer forbids one, spring and autumn leave it open.</summary>
    public static WeatherConditions FromSeason(DateOnly date)
    {
        var season = SeasonOf(date);
        var outerwear = season switch
        {
            Season.Winter => OuterwearPolicy.Required,
            Season.Summer => OuterwearPolicy.Forbidden,
            _ => OuterwearPolicy.Optional,
        };

        return new WeatherConditions(null, false, null, season, outerwear, Leaves(), null);
    }

    /// <summary>
    /// No day at all: "how do I wear this?" is about the piece, not about tomorrow, so nothing is banned, every season fits and a layer
    /// is optional.
    /// </summary>
    public static readonly WeatherConditions Neutral = new(null, false, null, Season.AllYear, OuterwearPolicy.Optional, new HashSet<string>(), null);

    /// <summary>Null when the weather rules the piece out; otherwise how well it suits the day (0..1).</summary>
    public static double? PieceFit(OutfitPiece piece, WeatherConditions weather)
    {
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(weather);

        if (weather.BannedLeaves.Contains(piece.Leaf))
        {
            return null;
        }

        if (piece.Slot == OutfitSlot.Outerwear && weather.OuterwearLeaves is { } allowed && !allowed.Contains(piece.Leaf))
        {
            return null;
        }

        var fit = (piece.Seasons & weather.Seasons) != 0 ? 1.0 : OffSeasonScore;

        if (weather.Rain && piece.Slot == OutfitSlot.Shoes)
        {
            fit *= RainShoeScores.GetValueOrDefault(piece.Leaf, 0.8);
            if (piece.Color == ProductColor.White)
            {
                fit *= RainWhiteShoesFactor;
            }
        }

        return fit;
    }

    private static HashSet<string> Leaves(params string[] leaves) => new(leaves, StringComparer.Ordinal);
}
