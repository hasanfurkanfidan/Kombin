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
    /// <summary>Open-Meteo forecasts 16 days; a week is all the app asks for.</summary>
    public const int MaxDaysAhead = 7;

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
        [property: JsonPropertyName("temperature_2m_max")] double?[]? TemperatureMax,
        [property: JsonPropertyName("temperature_2m_min")] double?[]? TemperatureMin,
        [property: JsonPropertyName("precipitation_probability_max")] int?[]? PrecipitationProbability,
        [property: JsonPropertyName("precipitation_sum")] double?[]? PrecipitationSum,
        [property: JsonPropertyName("weather_code")] int?[]? WeatherCode);
}
