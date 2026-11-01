using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Seed;

namespace Kombin.Api.IntegrationTests;

internal static class ProfileClientExtensions
{
    /// <summary>The cards exactly as <c>style-cards.json</c> describes them; the same IDs the seeded database has.</summary>
    public static readonly IReadOnlyList<StyleCard> StyleCards = StyleCardCatalog.Load().ToEntities();

    public static Task<HttpResponseMessage> CreatePersonaRawAsync(this HttpClient client, string name, object? extra = null, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["icon"] = "briefcase" };

        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[property.Name] = property.GetValue(extra);
            }
        }

        return client.PostAsJsonAsync("/api/v1/me/personas", body, cancellationToken);
    }

    /// <summary>Creates a persona, asserts a 201 and returns its ID.</summary>
    public static async Task<Guid> CreatePersonaAsync(this HttpClient client, string name, object? extra = null, CancellationToken cancellationToken = default)
    {
        using var response = await client.CreatePersonaRawAsync(name, extra, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(cancellationToken));

        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> SwipeRawAsync(this HttpClient client, Guid personaId, IEnumerable<(Guid CardId, string Decision)> swipes, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync(
            $"/api/v1/me/personas/{personaId}/swipes",
            new { swipes = swipes.Select(swipe => new { styleCardId = swipe.CardId, decision = swipe.Decision }) },
            cancellationToken);

    /// <summary>Swipes, asserts a 200 and returns the body.</summary>
    public static async Task<JsonElement> SwipeAsync(this HttpClient client, Guid personaId, IEnumerable<(Guid CardId, string Decision)> swipes, CancellationToken cancellationToken = default)
    {
        using var response = await client.SwipeRawAsync(personaId, swipes, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static Task<JsonElement> GetCardsAsync(this HttpClient client, Guid personaId, int? limit = null, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync($"/api/v1/me/personas/{personaId}/style-cards{(limit is null ? string.Empty : $"?limit={limit}")}", cancellationToken);

    /// <summary>The cards of the deck whose colors contain the given color; handy for teaching a persona a taste.</summary>
    public static IEnumerable<Guid> CardsWithColor(string color) =>
        StyleCards.Where(card => card.Colors.Contains(color, StringComparer.Ordinal)).Select(card => card.Id);
}
