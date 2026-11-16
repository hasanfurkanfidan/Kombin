using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.League;

namespace Kombin.Api.IntegrationTests;

internal static class LeagueClientExtensions
{
    public const string Me = "/api/v1/league/me";

    public static Task<HttpResponseMessage> JoinLeagueAsync(this HttpClient client, string? consentVersion, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync("/api/v1/league/join", new { consentVersion }, cancellationToken);

    /// <summary>Joins with the current consent version, asserts a 200 and returns the league state.</summary>
    public static async Task<JsonElement> JoinLeagueOkAsync(this HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.JoinLeagueAsync(LeagueConsent.Current.Version, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST /league/join returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static Task<HttpResponseMessage> LeaveLeagueAsync(this HttpClient client, CancellationToken cancellationToken) =>
        client.PostAsync("/api/v1/league/leave", null, cancellationToken);
}
