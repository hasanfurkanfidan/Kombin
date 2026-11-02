using Kombin.Api.Features.Recommendation.Weather;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The weather for the tests. Like the other fakes it keeps no per-test script: the forecast depends only on the city's name.
/// <list type="bullet">
/// <item><c>Rainville</c>: a mild, rainy day (14-19 °C, 80 %).</item>
/// <item><c>Coldville</c>: a cold day (-2-4 °C).</item>
/// <item><c>Hotville</c>: a hot day (24-33 °C).</item>
/// <item><c>Istanbul</c>: a mild, dry day (10-17 °C).</item>
/// <item>anything else: no forecast (the season decides).</item>
/// </list>
/// </summary>
internal sealed class FakeWeatherProvider : IWeatherProvider
{
    public Task<WeatherSnapshot?> GetAsync(string city, DateOnly date, CancellationToken cancellationToken) =>
        Task.FromResult<WeatherSnapshot?>(city.Trim() switch
        {
            "Rainville" => new WeatherSnapshot(city, date, 14, 19, 80, 6.5, 63),
            "Coldville" => new WeatherSnapshot(city, date, -2, 4, 10, 0, 3),
            "Hotville" => new WeatherSnapshot(city, date, 24, 33, 0, 0, 0),
            "Istanbul" => new WeatherSnapshot(city, date, 10, 17, 10, 0, 2),
            _ => null,
        });
}
