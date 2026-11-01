using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kombin.Api.IntegrationTests;

public sealed class NotificationPreferenceTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task FirstReadCreatesTheDefaults()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var preferences = await user.Client.GetJsonAsync("/api/v1/me/notification-preferences", Ct);

        Assert.True(preferences.GetProperty("priceAlerts").GetBoolean());
        Assert.True(preferences.GetProperty("stockAlerts").GetBoolean());
        Assert.True(preferences.GetProperty("recommendations").GetBoolean());
        Assert.Equal(3, preferences.GetProperty("maxRecommendationsPerWeek").GetInt32());
        Assert.Equal(JsonValueKind.Null, preferences.GetProperty("quietHoursStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, preferences.GetProperty("quietHoursEnd").ValueKind);
        Assert.Equal("Europe/Istanbul", preferences.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task APartialUpdateChangesOnlyTheGivenFields()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutPreferencesAsync(new { priceAlerts = false, maxRecommendationsPerWeek = 5 }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync(response);
        Assert.False(updated.GetProperty("priceAlerts").GetBoolean());
        Assert.True(updated.GetProperty("stockAlerts").GetBoolean());
        Assert.Equal(5, updated.GetProperty("maxRecommendationsPerWeek").GetInt32());

        var stored = await user.Client.GetJsonAsync("/api/v1/me/notification-preferences", Ct);
        Assert.False(stored.GetProperty("priceAlerts").GetBoolean());
        Assert.Equal(5, stored.GetProperty("maxRecommendationsPerWeek").GetInt32());
    }

    [Fact]
    public async Task QuietHoursAreStoredAsAPairAndCanBeCleared()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var set = await user.Client.PutPreferencesAsync(new { quietHoursStart = "23:00", quietHoursEnd = "08:30" }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var stored = await ReadAsync(set);
        Assert.Equal("23:00", stored.GetProperty("quietHoursStart").GetString());
        Assert.Equal("08:30", stored.GetProperty("quietHoursEnd").GetString());

        // Other fields do not touch them.
        using var other = await user.Client.PutPreferencesAsync(new { stockAlerts = false }, Ct);
        Assert.Equal("23:00", (await ReadAsync(other)).GetProperty("quietHoursStart").GetString());

        using var cleared = await user.Client.PutPreferencesAsync(new { clearQuietHours = true }, Ct);
        var after = await ReadAsync(cleared);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("quietHoursStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("quietHoursEnd").ValueKind);
    }

    [Theory]
    [InlineData("22:00", null)]
    [InlineData(null, "08:00")]
    public async Task QuietHoursWithOnlyOneEndAreRejected(string? start, string? end)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutPreferencesAsync(new { quietHoursStart = start, quietHoursEnd = end }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Notification.QuietHoursIncomplete", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public async Task TheWeeklyLimitMustBeBetweenZeroAndSeven(int limit)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutPreferencesAsync(new { maxRecommendationsPerWeek = limit }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("maxRecommendationsPerWeek", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task TheWeeklyLimitBoundsAreAllowed(int limit)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutPreferencesAsync(new { maxRecommendationsPerWeek = limit }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("25:00", "08:00", "quietHoursStart")]
    [InlineData("22:00", "8am", "quietHoursEnd")]
    [InlineData("22:00", "22:00", "quietHoursEnd")]
    public async Task BadQuietHoursAreValidationErrors(string start, string end, string field)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutPreferencesAsync(new { quietHoursStart = start, quietHoursEnd = end }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task ClearingCannotBeCombinedWithNewQuietHours()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutPreferencesAsync(new { clearQuietHours = true, quietHoursStart = "22:00", quietHoursEnd = "08:00" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PreferencesNeedAuthentication()
    {
        using var anonymous = factory.CreateAnonymousClient();

        using var get = await anonymous.GetAsync("/api/v1/me/notification-preferences", Ct);
        using var put = await anonymous.PutPreferencesAsync(new { priceAlerts = false }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }
}
