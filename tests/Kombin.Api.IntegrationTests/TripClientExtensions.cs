using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kombin.Api.IntegrationTests;

/// <summary>Helpers for budget outfits and trips.</summary>
internal static class TripClientExtensions
{
    public const string Trips = "/api/v1/me/trips";

    public const string Budget = "/api/v1/me/outfits/budget";

    public const string AnonymousBudget = "/api/v1/outfits/budget";

    /// <summary>The first day of a test trip: the day after tomorrow, safely "from today" in Istanbul whatever the UTC hour.</summary>
    public static DateOnly FirstDay => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);

    public static Dictionary<string, object?> TripRequest(string city, int days, int offsetDays = 2, object? occasions = null, int? maxPieces = null, bool? allowRepeatShoes = null, bool? laundry = null, Guid? personaId = null)
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offsetDays);
        var body = new Dictionary<string, object?>
        {
            ["city"] = city,
            ["startDate"] = start,
            ["endDate"] = start.AddDays(days - 1),
        };

        if (occasions is not null)
        {
            body["occasions"] = occasions;
        }

        if (maxPieces is not null)
        {
            body["maxPieces"] = maxPieces;
        }

        if (allowRepeatShoes is not null)
        {
            body["allowRepeatShoes"] = allowRepeatShoes;
        }

        if (laundry is not null)
        {
            body["laundry"] = laundry;
        }

        if (personaId is not null)
        {
            body["personaId"] = personaId;
        }

        return body;
    }

    /// <summary>A neutral top, bottom, pair of shoes and layer at one formality and price, each from its own new brand: enough for a catalog outfit.</summary>
    public static async Task SeedCapsuleAsync(this HttpClient client, int formality, decimal price = 300m, string audience = "Unisex", CancellationToken cancellationToken = default)
    {
        await client.SeedProductAsync("ust-giyim/tisort", "White", formality, price, audience, cancellationToken: cancellationToken);
        await client.SeedProductAsync("alt-giyim/pantolon", "Black", formality, price, audience, cancellationToken: cancellationToken);
        await client.SeedProductAsync("ayakkabi/sneaker", "White", formality, price, audience, cancellationToken: cancellationToken);
        await client.SeedProductAsync("dis-giyim/ceket", "Navy", formality, price, audience, cancellationToken: cancellationToken);
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> PostJsonAsync(this HttpClient client, string url, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(url, body, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static async Task<JsonElement> PlanTripOkAsync(this HttpClient client, object request, CancellationToken cancellationToken)
    {
        var (status, body) = await client.PostJsonAsync($"{Trips}/plan", request, cancellationToken);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        return body;
    }

    public static Task<(HttpStatusCode Status, JsonElement Body)> SaveTripAsync(this HttpClient client, string name, object request, CancellationToken cancellationToken) =>
        client.PostJsonAsync(Trips, new { name, request }, cancellationToken);

    public static async Task<JsonElement> BudgetOkAsync(this HttpClient client, object request, CancellationToken cancellationToken, bool anonymous = false)
    {
        var (status, body) = await client.PostJsonAsync(anonymous ? AnonymousBudget : Budget, request, cancellationToken);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        return body;
    }
}
