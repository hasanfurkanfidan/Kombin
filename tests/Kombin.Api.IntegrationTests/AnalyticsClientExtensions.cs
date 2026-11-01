using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Analytics;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

internal static class AnalyticsClientExtensions
{
    public static string NewAnonymousId() => $"anon-{Guid.NewGuid():N}";

    /// <summary>One event of a batch, shaped as the app sends it. Every call gets its own session unless one is given.</summary>
    public static object Event(
        string name = "app_open",
        object? properties = null,
        string? sessionId = null,
        string platform = "Ios",
        DateTimeOffset? occurredAt = null,
        string? anonymousId = null) =>
        new
        {
            name,
            properties,
            sessionId = sessionId ?? $"session-{Guid.NewGuid():N}",
            platform,
            appVersion = "1.0.0",
            occurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            anonymousId,
        };

    public static Task<HttpResponseMessage> PostEventsAsync(this HttpClient client, object[] events, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync("/api/v1/events", new { events }, cancellationToken);

    /// <summary>Posts a batch, asserts a 202 and returns the body (<c>accepted</c>, <c>rejected</c>).</summary>
    public static async Task<JsonElement> PostEventsOkAsync(this HttpClient client, object[] events, CancellationToken cancellationToken)
    {
        using var response = await client.PostEventsAsync(events, cancellationToken);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static Task<List<AnalyticsEvent>> EventsOfAnonymousIdAsync(this ApiFactory factory, string anonymousId, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.AnalyticsEvents.AsNoTracking().Where(stored => stored.AnonymousId == anonymousId).OrderBy(stored => stored.Id).ToListAsync(cancellationToken));
}
