using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kombin.Api.Common.Time;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Recommendation.Weather;

/// <summary>
/// Open-Meteo (free, no key): the city is geocoded once a day, the daily forecast is cached per city, day and hour, so a busy hour
/// costs one call per city. Failures are logged and answered with null: the outfit then follows the season.
/// </summary>
internal sealed partial class OpenMeteoWeatherProvider(
    HttpClient http, IMemoryCache cache, IClock clock, IOptions<WeatherOptions> options, ILogger<OpenMeteoWeatherProvider> logger) : IWeatherProvider
{
    /// <summary>Open-Meteo forecasts 16 days; a week is all the daily outfit asks for.</summary>
    public const int MaxDaysAhead = 7;

    /// <summary>Trip days up to today + 15 use the forecast (Open-Meteo's 16 days); later ones the climate normals.</summary>
    public const int ForecastHorizonDays = 16;

    /// <summary>The climate normals of a day: the same calendar day in this many previous years, averaged.</summary>
    public const int NormalYears = 3;

    private const string ArchiveFields = "temperature_2m_max,temperature_2m_min,precipitation_sum";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>Days are Istanbul days, like every outfit date (the IANA name; Windows hosts name the zone differently).</summary>
    private const string TimeZone = "Europe/Istanbul";

    private const string DailyFields = "temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,weather_code";

    public static void Configure(HttpClient client) => client.Timeout = Timeout;

    public async Task<WeatherSnapshot?> GetAsync(string city, DateOnly date, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(city))
        {
            return null;
        }

        var name = city.Trim();
        var now = clock.UtcNow;
        var key = string.Create(CultureInfo.InvariantCulture, $"weather:{name.ToUpperInvariant()}:{date:yyyy-MM-dd}:{now:yyyyMMddHH}");

        if (cache.TryGetValue(key, out WeatherSnapshot? cached))
        {
            return cached;
        }

        try
        {
            var location = await LocateAsync(name, cancellationToken);
            var snapshot = location is null ? null : await ForecastAsync(name, location, date, cancellationToken);

            cache.Set(key, snapshot, TimeSpan.FromHours(1));
            return snapshot;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Not cached: the next request tries again.
            LogWeatherFailed(logger, exception, name);
            return null;
        }
    }

    public async Task<IReadOnlyList<DayWeather>> GetDaysAsync(string city, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var days = new List<DateOnly>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            days.Add(day);
        }

        var season = days.Select(day => new DayWeather(day, WeatherSource.Season, null)).ToList();
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(city) || days.Count == 0)
        {
            return season;
        }

        var name = city.Trim();
        var now = clock.UtcNow;
        var lastForecastDay = Wardrobe.WearRules.Today(now).AddDays(ForecastHorizonDays - 1);

        try
        {
            var location = await LocateAsync(name, cancellationToken);
            if (location is null)
            {
                return season;
            }

            var forecastDays = days.Where(day => day <= lastForecastDay).ToList();
            var normalDays = days.Where(day => day > lastForecastDay).ToList();
            var forecast = forecastDays.Count == 0 ? [] : await ForecastRangeAsync(name, location, forecastDays[0], forecastDays[^1], now, cancellationToken);
            var normals = normalDays.Count == 0 ? [] : await NormalsAsync(name, location, normalDays[0], normalDays[^1], cancellationToken);

            return
            [
                .. days.Select(day => forecast.TryGetValue(day, out var predicted)
                    ? new DayWeather(day, WeatherSource.Forecast, predicted)
                    : normals.TryGetValue(day, out var normal)
                        ? new DayWeather(day, WeatherSource.Normal, normal)
                        : new DayWeather(day, WeatherSource.Season, null)),
            ];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            LogWeatherFailed(logger, exception, name);
            return season;
        }
    }

    /// <summary>One forecast call for the range, cached per city, range and hour like the single days.</summary>
    private async Task<Dictionary<DateOnly, WeatherSnapshot>> ForecastRangeAsync(
        string city, GeoResult location, DateOnly start, DateOnly end, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"weather-range:{city.ToUpperInvariant()}:{start:yyyy-MM-dd}:{end:yyyy-MM-dd}:{now:yyyyMMddHH}");
        if (cache.TryGetValue(key, out Dictionary<DateOnly, WeatherSnapshot>? cached) && cached is not null)
        {
            return cached;
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{options.Value.ForecastBaseUrl.TrimEnd('/')}/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}&daily={DailyFields}&timezone={Uri.EscapeDataString(TimeZone)}&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}");
        var daily = (await http.GetFromJsonAsync<ForecastResponse>(url, cancellationToken))?.Daily;
        var result = new Dictionary<DateOnly, WeatherSnapshot>();

        for (var index = 0; index < (daily?.Time?.Length ?? 0); index++)
        {
            if (!DateOnly.TryParseExact(daily!.Time![index], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || At(daily.TemperatureMax, index) is not { } max || At(daily.TemperatureMin, index) is not { } min)
            {
                continue;
            }

            result[day] = new WeatherSnapshot(city, day, min, max, At(daily.PrecipitationProbability, index), At(daily.PrecipitationSum, index), At(daily.WeatherCode, index));
        }

        cache.Set(key, result, TimeSpan.FromHours(1));
        return result;
    }

    /// <summary>
    /// The climate normals of the range: the same calendar days in each of the previous <see cref="NormalYears"/> years from the archive, averaged
    /// (a 29 February falls back to the 28th). The chance of rain is the share of those years with at least 1 mm. Cached for a week.
    /// </summary>
    private async Task<Dictionary<DateOnly, WeatherSnapshot>> NormalsAsync(
        string city, GeoResult location, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"weather-normals:{city.ToUpperInvariant()}:{start:yyyy-MM-dd}:{end:yyyy-MM-dd}");
        if (cache.TryGetValue(key, out Dictionary<DateOnly, WeatherSnapshot>? cached) && cached is not null)
        {
            return cached;
        }

        var samples = new Dictionary<DateOnly, List<(double Min, double Max, double? Rain)>>();
        for (var back = 1; back <= NormalYears; back++)
        {
            var from = start.AddYears(-back);
            var to = end.AddYears(-back);
            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"{options.Value.ArchiveBaseUrl.TrimEnd('/')}/v1/archive?latitude={location.Latitude}&longitude={location.Longitude}&daily={ArchiveFields}&timezone={Uri.EscapeDataString(TimeZone)}&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}");
            var daily = (await http.GetFromJsonAsync<ForecastResponse>(url, cancellationToken))?.Daily;

            for (var index = 0; index < (daily?.Time?.Length ?? 0); index++)
            {
                if (!DateOnly.TryParseExact(daily!.Time![index], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var past)
                    || At(daily.TemperatureMax, index) is not { } max || At(daily.TemperatureMin, index) is not { } min)
                {
                    continue;
                }

                var target = past.AddYears(back);
                if (target < start || target > end)
                {
                    continue;
                }

                if (!samples.TryGetValue(target, out var list))
                {
                    samples[target] = list = [];
                }

                list.Add((min, max, At(daily.PrecipitationSum, index)));
            }
        }

        var result = samples.ToDictionary(
            entry => entry.Key,
            entry =>
            {
                var rains = entry.Value.Select(sample => sample.Rain).OfType<double>().ToList();

                return new WeatherSnapshot(
                    city,
                    entry.Key,
                    Math.Round(entry.Value.Average(sample => sample.Min), 1),
                    Math.Round(entry.Value.Average(sample => sample.Max), 1),
                    rains.Count == 0 ? null : (int)Math.Round(100.0 * rains.Count(rain => rain >= WeatherRules.RainMillimetres) / rains.Count),
                    rains.Count == 0 ? null : Math.Round(rains.Average(), 1),
                    null);
            });

        cache.Set(key, result, TimeSpan.FromDays(7));
        return result;
    }

    private static T? At<T>(T?[]? values, int index)
        where T : struct =>
        values is not null && index < values.Length ? values[index] : null;

    private async Task<GeoResult?> LocateAsync(string city, CancellationToken cancellationToken)
    {
        var key = $"geocode:{city.ToUpperInvariant()}";
        if (cache.TryGetValue(key, out GeoResult? cached))
        {
            return cached;
        }

        var url = $"{options.Value.GeocodingBaseUrl.TrimEnd('/')}/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=tr&format=json";
        var response = await http.GetFromJsonAsync<GeoResponse>(url, cancellationToken);
        var location = response?.Results?.FirstOrDefault();

        cache.Set(key, location, TimeSpan.FromDays(1));
        return location;
    }

    private async Task<WeatherSnapshot?> ForecastAsync(string city, GeoResult location, DateOnly date, CancellationToken cancellationToken)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{options.Value.ForecastBaseUrl.TrimEnd('/')}/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}&daily={DailyFields}&timezone={Uri.EscapeDataString(TimeZone)}&start_date={date:yyyy-MM-dd}&end_date={date:yyyy-MM-dd}");

        var response = await http.GetFromJsonAsync<ForecastResponse>(url, cancellationToken);
        var daily = response?.Daily;

        if (daily?.TemperatureMax is not [{ } max, ..] || daily.TemperatureMin is not [{ } min, ..])
        {
            return null;
        }

        return new WeatherSnapshot(
            city,
            date,
            min,
            max,
            daily.PrecipitationProbability is [var probability, ..] ? probability : null,
            daily.PrecipitationSum is [var sum, ..] ? sum : null,
            daily.WeatherCode is [var code, ..] ? code : null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Weather for {City} could not be fetched; the outfit follows the season.")]
    private static partial void LogWeatherFailed(ILogger logger, Exception exception, string city);

    private sealed record GeoResponse([property: JsonPropertyName("results")] List<GeoResult>? Results);

    private sealed record GeoResult([property: JsonPropertyName("latitude")] double Latitude, [property: JsonPropertyName("longitude")] double Longitude);

    private sealed record ForecastResponse([property: JsonPropertyName("daily")] DailyForecast? Daily);

    private sealed record DailyForecast(
        [property: JsonPropertyName("time")] string[]? Time,
        [property: JsonPropertyName("temperature_2m_max")] double?[]? TemperatureMax,
        [property: JsonPropertyName("temperature_2m_min")] double?[]? TemperatureMin,
        [property: JsonPropertyName("precipitation_probability_max")] int?[]? PrecipitationProbability,
        [property: JsonPropertyName("precipitation_sum")] double?[]? PrecipitationSum,
        [property: JsonPropertyName("weather_code")] int?[]? WeatherCode);
}
