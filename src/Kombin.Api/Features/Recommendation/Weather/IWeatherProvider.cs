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

/// <summary>Where a day's weather came from: the forecast, the climate normals of that calendar day, or nothing (the season decides).</summary>
internal enum WeatherSource
{
    Forecast,
    Normal,
    Season,
}

/// <summary>One day of a range. <c>Snapshot</c> is null exactly when <c>Source</c> is <see cref="WeatherSource.Season"/>.</summary>
internal sealed record DayWeather(DateOnly Date, WeatherSource Source, WeatherSnapshot? Snapshot);

/// <summary>Never throws: a failure means no forecast (null) or <see cref="WeatherSource.Season"/> days.</summary>
internal interface IWeatherProvider
{
    /// <summary>The forecast for a city and day, or null when there is none (disabled, unknown city, outside the forecast range, the service down).</summary>
    Task<WeatherSnapshot?> GetAsync(string city, DateOnly date, CancellationToken cancellationToken);

    /// <summary>
    /// Every day from <paramref name="start"/> to <paramref name="end"/>, in order: the forecast for days within its horizon, the climate normals
    /// (the same calendar days of past years, averaged) beyond it, <see cref="WeatherSource.Season"/> where neither exists. For trip planning.
    /// </summary>
    Task<IReadOnlyList<DayWeather>> GetDaysAsync(string city, DateOnly start, DateOnly end, CancellationToken cancellationToken);
}

internal sealed class WeatherOptions
{
    public const string SectionName = "Weather";

    /// <summary>Off by default; compose and Development turn it on. Off means outfits follow the season of the date.</summary>
    public bool Enabled { get; set; }

    public string GeocodingBaseUrl { get; set; } = "https://geocoding-api.open-meteo.com";

    public string ForecastBaseUrl { get; set; } = "https://api.open-meteo.com";

    /// <summary>Past days, for the climate normals of trip days beyond the forecast horizon.</summary>
    public string ArchiveBaseUrl { get; set; } = "https://archive-api.open-meteo.com";
}
