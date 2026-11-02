namespace Kombin.Api.Features.Recommendation.Weather;

/// <summary>A day's forecast for a city, as stored with the outfit and shown to the app. Temperatures in °C.</summary>
internal sealed record WeatherSnapshot(
    string City,
    DateOnly Date,
    double TempMinC,
    double TempMaxC,
    int? PrecipitationProbability,
    double? PrecipitationMm,
    int? WeatherCode);

/// <summary>The forecast for a city and day, or null when there is none (disabled, unknown city, outside the forecast range, the service down). Never throws.</summary>
internal interface IWeatherProvider
{
    Task<WeatherSnapshot?> GetAsync(string city, DateOnly date, CancellationToken cancellationToken);
}

internal sealed class WeatherOptions
{
    public const string SectionName = "Weather";

    /// <summary>Off by default; compose and Development turn it on. Off means outfits follow the season of the date.</summary>
    public bool Enabled { get; set; }

    public string GeocodingBaseUrl { get; set; } = "https://geocoding-api.open-meteo.com";

    public string ForecastBaseUrl { get; set; } = "https://api.open-meteo.com";
}
