using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Analytics.Jobs;
using Kombin.Api.Persistence.Entities.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The four beta metrics against a real database. The cohort lives in March 2020: the events are inserted with those dates directly,
/// which no request can do (the API clamps old clocks), so nothing else in the shared database falls into these cohort weeks.
/// </summary>
public sealed class WeeklyMetricsTests(ApiFactory factory)
{
    // Monday 2020-03-30 08:00 in Istanbul (UTC+3). The four cohort weeks it covers start on 2020-03-23, 03-16, 03-09 and 03-02.
    private static readonly DateTimeOffset AsOf = new(2020, 3, 30, 5, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Istanbul = TimeSpan.FromHours(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset March(int day, int hour = 12, int minute = 0) => new(2020, 3, day, hour, minute, 0, Istanbul);

    private static AnalyticsEvent Open(DateTimeOffset at, Guid? userId = null, string? anonymousId = null) =>
        Event("app_open", at, userId, anonymousId);

    private static AnalyticsEvent Event(string name, DateTimeOffset at, Guid? userId, string? anonymousId, string properties = "{}") => new()
    {
        UserId = userId,
        AnonymousId = anonymousId,
        Name = name,
        Properties = properties,
        SessionId = $"metrics-{Guid.NewGuid():N}",
        Platform = AnalyticsPlatform.Ios,
        AppVersion = "1.0.0",
        OccurredAt = at.ToUniversalTime(),
        ReceivedAt = at.ToUniversalTime(),
    };

    private static AnalyticsEvent Onboarding(DateTimeOffset at, string action, Guid? userId = null, string? anonymousId = null) =>
        Event("onboarding_step", at, userId, anonymousId, $$"""{"step":"style_cards","action":"{{action}}"}""");

    private static readonly Lock SeedLock = new();

    private static Task? SeededCohort;

    /// <summary>The tests share one database and every one of them looks at the same cohort, so it is seeded once, by whichever test runs first.</summary>
    private Task EnsureSeededAsync()
    {
        lock (SeedLock)
        {
            return SeededCohort ??= SeedCohortAsync();
        }
    }

    /// <summary>
    /// The week of 2020-03-02 has six people:
    /// U1 (returns on day 7, has an alert, a click, finishes onboarding), U2 (day 7, starts onboarding), U3 (never comes back on days 6-8,
    /// created an alert that no longer exists), A4 (anonymous, day 8, skips onboarding), A5 (anonymous, day 9: too late) and A6 (anonymous,
    /// day 5). Outside it: A7 opens at 00:30 on 2020-03-09 (the next week), U8 first opened on 2020-02-28 (the week before),
    /// A9 first opens on 2020-03-24, too late for anyone to have reached day 8 by AsOf.
    /// </summary>
    private async Task SeedCohortAsync()
    {
        using var u1 = await factory.CreateUserAsync(Ct);
        using var u2 = await factory.CreateUserAsync(Ct);
        using var u3 = await factory.CreateUserAsync(Ct);
        using var u8 = await factory.CreateUserAsync(Ct);

        var product = await factory.SeedProductAsync(u1.Client, 100m, cancellationToken: Ct);
        await u1.Client.CreateAlertOkAsync(product.ProductId, "AnyChange", cancellationToken: Ct);

        // Two clicks of the same user count once; the click of an anonymous caller belongs to nobody.
        using var firstClick = await u1.Client.GetAsync($"/api/v1/go/{product.OfferId}?src=feed", Ct);
        using var secondClick = await u1.Client.GetAsync($"/api/v1/go/{product.OfferId}?src=feed", Ct);
        using var anonymous = factory.CreateAnonymousClient();
        using var anonymousClick = await anonymous.GetAsync($"/api/v1/go/{product.OfferId}?src=feed", Ct);
        Assert.Equal(HttpStatusCode.Found, firstClick.StatusCode);
        Assert.Equal(HttpStatusCode.Found, anonymousClick.StatusCode);

        string a4 = $"a4-{Guid.NewGuid():N}", a5 = $"a5-{Guid.NewGuid():N}", a6 = $"a6-{Guid.NewGuid():N}", a7 = $"a7-{Guid.NewGuid():N}", a9 = $"a9-{Guid.NewGuid():N}";

        await factory.WithDbAsync(async db =>
        {
            db.AnalyticsEvents.AddRange(
                Open(March(3), u1.Id), Open(March(10), u1.Id), Onboarding(March(3, 13), "done", u1.Id),
                Open(March(4), u2.Id), Open(March(11), u2.Id), Onboarding(March(4, 13), "next", u2.Id),
                Open(March(5), u3.Id), Open(March(9), u3.Id), Open(March(20), u3.Id), Event("alert_created", March(6), u3.Id, null, """{"kind":"PriceDrop"}"""),
                Open(March(6), anonymousId: a4), Open(March(14), anonymousId: a4), Onboarding(March(6, 13), "skip", anonymousId: a4),
                Open(March(7), anonymousId: a5), Open(March(16), anonymousId: a5),
                Open(March(8, 23, 59), anonymousId: a6), Open(March(13), anonymousId: a6),
                Open(March(9, 0, 30), anonymousId: a7),
                Open(new DateTimeOffset(2020, 2, 28, 12, 0, 0, Istanbul), u8.Id), Open(March(4), u8.Id),
                Open(March(24), anonymousId: a9));

            return await db.SaveChangesAsync(Ct);
        });
    }

    private async Task<JsonElement> ComputeAndReadAsync()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<WeeklyMetricsJob>().ComputeAsync(AsOf, Ct);
        }

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/internal/analytics/weekly?from=2020-03-01");
        request.Headers.Add("X-Internal-Key", ApiFactory.InternalApiKey);
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items");
    }

    private static JsonElement? Row(JsonElement items, string weekStart, string metric) =>
        items.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(item => item!.Value.GetProperty("weekStart").GetString() == weekStart && item.Value.GetProperty("metric").GetString() == metric);

    private static void AssertRow(JsonElement items, string weekStart, string metric, long numerator, long denominator, decimal value)
    {
        var row = Row(items, weekStart, metric);
        Assert.True(row is not null, $"There is no {metric} row for the week of {weekStart}.");
        Assert.Equal(numerator, row.Value.GetProperty("numerator").GetInt64());
        Assert.Equal(denominator, row.Value.GetProperty("denominator").GetInt64());
        Assert.Equal(value, row.Value.GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task TheFourMetricsOfATinyCohortComeOutRight()
    {
        await EnsureSeededAsync();

        var items = await ComputeAndReadAsync();

        // Returned on day 6-8: U1 (day 7), U2 (day 7), A4 (day 8) of the six; A5 came on day 9, A6 on day 5, U3 never within the window.
        AssertRow(items, "2020-03-02", "d7_retention", 3, 6, 50m);

        // An alert in engagement.price_alerts (U1) or an alert_created event (U3).
        AssertRow(items, "2020-03-02", "alert_rate", 2, 6, 33.33m);

        // Only U1 clicked while signed in; the second click does not count twice, the anonymous click belongs to nobody.
        AssertRow(items, "2020-03-02", "click_rate", 1, 6, 16.67m);

        // Started onboarding: U1, U2 and A4. Finished: U1.
        AssertRow(items, "2020-03-02", "onboarding_completion", 1, 3, 33.33m);
    }

    [Fact]
    public async Task OnlyPeopleWhoseFirstOpenFallsInTheWeekBelongToItsCohort()
    {
        await EnsureSeededAsync();

        var items = await ComputeAndReadAsync();

        // A7 (opened at 00:30 on Monday 03-09, Istanbul) is alone in the next week; U8 (first open 02-28) is in no week of this run.
        AssertRow(items, "2020-03-09", "d7_retention", 0, 1, 0m);
        AssertRow(items, "2020-03-09", "alert_rate", 0, 1, 0m);
        Assert.Null(Row(items, "2020-03-09", "onboarding_completion"));
    }

    [Fact]
    public async Task ACohortTooYoungForDayEightHasNoRetentionRowButTheOtherMetricsAreThere()
    {
        await EnsureSeededAsync();

        var items = await ComputeAndReadAsync();

        // A9 first opened on 03-24; day 8 is 04-01, after AsOf. Nobody of that week has matured, so there is nothing to say.
        Assert.Null(Row(items, "2020-03-23", "d7_retention"));
        AssertRow(items, "2020-03-23", "alert_rate", 0, 1, 0m);
        AssertRow(items, "2020-03-23", "click_rate", 0, 1, 0m);
    }

    [Fact]
    public async Task ARerunOverwritesTheRowsInsteadOfAddingMore()
    {
        await EnsureSeededAsync();
        await ComputeAndReadAsync();
        var before = await factory.WithDbAsync(db => db.WeeklyMetrics.CountAsync(row => row.WeekStart >= new DateOnly(2020, 3, 1) && row.WeekStart < new DateOnly(2020, 4, 1), Ct));

        var items = await ComputeAndReadAsync();

        var after = await factory.WithDbAsync(db => db.WeeklyMetrics.CountAsync(row => row.WeekStart >= new DateOnly(2020, 3, 1) && row.WeekStart < new DateOnly(2020, 4, 1), Ct));
        Assert.Equal(before, after);
        Assert.Equal(after, items.GetArrayLength());
        AssertRow(items, "2020-03-02", "d7_retention", 3, 6, 50m);
        Assert.Equal(AsOf, Row(items, "2020-03-02", "d7_retention")!.Value.GetProperty("computedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task TheEndpointListsNewestWeekFirstAndNeedsTheInternalKey()
    {
        await EnsureSeededAsync();
        var items = await ComputeAndReadAsync();

        var weeks = items.EnumerateArray().Select(item => item.GetProperty("weekStart").GetString()!).ToList();
        Assert.Equal(weeks.OrderByDescending(week => week, StringComparer.Ordinal), weeks);

        using var client = factory.CreateClient();
        using var withoutKey = await client.GetAsync("/api/v1/internal/analytics/weekly", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);

        // Without ?from only the last twelve weeks are listed, so the 2020 rows are not there.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/internal/analytics/weekly");
        request.Headers.Add("X-Internal-Key", ApiFactory.InternalApiKey);
        using var recent = await client.SendAsync(request, Ct);
        var recentItems = (await recent.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items");
        Assert.DoesNotContain(recentItems.EnumerateArray(), item => item.GetProperty("weekStart").GetString()!.StartsWith("2020", StringComparison.Ordinal));
    }
}
