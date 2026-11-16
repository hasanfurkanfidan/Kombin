using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Notification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Night review item 6: the validator and the handler each read the clock. A request for "today" that is valid at 23:59:59.95 in Istanbul must
/// not crash when the day has turned a moment later: the answer is the same 400 the validator gives.
/// </summary>
public sealed class MidnightEdgeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The real clock, until armed: then the first read is just before the next Istanbul midnight and every later one just after it.</summary>
    private sealed class MidnightClock : IClock
    {
        private int _reads = -1;

        public DateTimeOffset Before { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                if (Volatile.Read(ref _reads) < 0)
                {
                    return DateTimeOffset.UtcNow;
                }

                return Interlocked.Increment(ref _reads) == 1 ? Before : Before.AddMilliseconds(100);
            }
        }

        public DateOnly DayBefore => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Before, QuietHours.Zone).DateTime);

        public string DayText => DayBefore.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        public void Arm()
        {
            var istanbul = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, QuietHours.Zone);
            var midnight = new DateTimeOffset(istanbul.Date.AddDays(1), istanbul.Offset);
            Before = midnight.AddMilliseconds(-50).ToUniversalTime();
            Volatile.Write(ref _reads, 0);
        }

        public void Disarm() => Volatile.Write(ref _reads, -1);
    }

    private (WebApplicationFactory<Program> Host, MidnightClock Clock) Host()
    {
        var clock = new MidnightClock();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        }));

        return (host, clock);
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        return client;
    }

    private static async Task AssertDateRejectedAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        Assert.Contains("'Date' must be today, tomorrow or a yyyy-MM-dd day", text, StringComparison.Ordinal);
        Assert.Contains("\"date\"", text, StringComparison.Ordinal);
        response.Dispose();
    }

    [Fact]
    public async Task TheAnonymousBudgetAnswers400WhenTheDayTurnsBetweenValidationAndHandler()
    {
        var (host, clock) = Host();
        using var _ = host;
        using var client = host.CreateClient();

        clock.Arm();
        try
        {
            await AssertDateRejectedAsync(await client.PostAsJsonAsync("/api/v1/outfits/budget", new { budget = 1000, audience = "Unisex", date = clock.DayText }, Ct));
        }
        finally
        {
            clock.Disarm();
        }
    }

    [Fact]
    public async Task TheSignedInBudgetAndTheDailyOutfitDoToo()
    {
        var (host, clock) = Host();
        using var _ = host;
        using var client = await SignedInAsync(host);

        clock.Arm();
        try
        {
            await AssertDateRejectedAsync(await client.PostAsJsonAsync("/api/v1/me/outfits/budget", new { budget = 1000, date = clock.DayText }, Ct));

            clock.Arm();
            await AssertDateRejectedAsync(await client.GetAsync($"/api/v1/me/outfits/daily?date={clock.DayText}", Ct));
        }
        finally
        {
            clock.Disarm();
        }
    }

    [Fact]
    public async Task ADateThatIsStillValidAfterTheMidnightIsAccepted()
    {
        var (host, clock) = Host();
        using var _ = host;
        using var client = host.CreateClient();

        clock.Arm();
        try
        {
            // "tomorrow" is read against the same two clocks and stays within the next days either way.
            using var response = await client.PostAsJsonAsync("/api/v1/outfits/budget", new { budget = 1000, audience = "Unisex", date = "tomorrow" }, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            clock.Disarm();
        }
    }
}
