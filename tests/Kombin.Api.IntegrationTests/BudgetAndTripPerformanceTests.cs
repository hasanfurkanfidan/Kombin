using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Latency budgets of budget outfits (p95 ≤ 600 ms, signed in with a 200-piece wardrobe and anonymous, against 50,000 products) and trip planning
/// (p95 ≤ 800 ms for 14 days and a 150-piece wardrobe). Same rules as <see cref="OutfitPerformanceTests"/>: synthetic seeded data on its own
/// PostgreSQL container, run only with <c>KOMBIN_PERF=1</c> (CI's perf step), skipped otherwise.
/// </summary>
[Collection(LatencyBudgets.Name)]
public sealed class BudgetAndTripPerformanceTests(OutfitPerformanceTests.PerformanceFactory factory) : IClassFixture<OutfitPerformanceTests.PerformanceFactory>
{
    public const int TripWardrobePieces = 150;

    public const int TripDays = 14;

    public static readonly TimeSpan BudgetBudget = TimeSpan.FromMilliseconds(600);

    public static readonly TimeSpan TripBudget = TimeSpan.FromMilliseconds(800);

    private static readonly int[] Budgets = [800, 1500, 3000, 7500, 20000];

    private static readonly string?[] Occasions = [null, "office", "weekend", "evening"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = OutfitPerformanceTests.SkipMessage, SkipUnless = nameof(OutfitPerformanceTests.Enabled), SkipType = typeof(OutfitPerformanceTests))]
    public async Task BudgetOutfitsStayWithinSixHundredMillisecondsAtP95()
    {
        var (client, _) = await factory.SeedAsync(Ct);
        using var anonymous = factory.CreateClient();

        object Body(int index, bool signedIn) => signedIn
            ? new { budget = Budgets[index % Budgets.Length], occasion = Occasions[index % Occasions.Length], seed = index % 3 }
            : new { budget = Budgets[index % Budgets.Length], occasion = Occasions[index % Occasions.Length], seed = index % 3, audience = index % 2 == 0 ? "Women" : "Men" };

        for (var index = 0; index < 5; index++)
        {
            await TimeAsync(client, TripClientExtensions.Budget, Body(index, true));
            await TimeAsync(anonymous, TripClientExtensions.AnonymousBudget, Body(index, false));
        }

        var signedIn = new List<double>();
        var visitor = new List<double>();
        for (var index = 0; index < OutfitPerformanceTests.Samples; index++)
        {
            signedIn.Add(await TimeAsync(client, TripClientExtensions.Budget, Body(index + 7, true)));
            visitor.Add(await TimeAsync(anonymous, TripClientExtensions.AnonymousBudget, Body(index + 7, false)));
        }

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"budget (signed in) p50 {Percentile(signedIn, 50):F1} ms  p95 {Percentile(signedIn, 95):F1} ms  max {signedIn.Max():F1} ms");
        output?.WriteLine($"budget (anonymous) p50 {Percentile(visitor, 50):F1} ms  p95 {Percentile(visitor, 95):F1} ms  max {visitor.Max():F1} ms");

        Assert.True(Percentile(signedIn, 95) <= BudgetBudget.TotalMilliseconds, $"signed-in budget p95 {Percentile(signedIn, 95):F1} ms");
        Assert.True(Percentile(visitor, 95) <= BudgetBudget.TotalMilliseconds, $"anonymous budget p95 {Percentile(visitor, 95):F1} ms");
    }

    [Fact(Skip = OutfitPerformanceTests.SkipMessage, SkipUnless = nameof(OutfitPerformanceTests.Enabled), SkipType = typeof(OutfitPerformanceTests))]
    public async Task AFourteenDayTripStaysWithinEightHundredMillisecondsAtP95()
    {
        await factory.SeedAsync(Ct);
        using var client = await factory.CreateUserWithWardrobeAsync(TripWardrobePieces, new Random(20261115), Ct);

        object Body(int index) => TripClientExtensions.TripRequest(
            index % 3 == 0 ? "Coldville" : index % 3 == 1 ? "Istanbul" : "Rainville",
            TripDays,
            offsetDays: 2 + (index % 5),
            laundry: index % 4 == 0,
            allowRepeatShoes: index % 2 == 0);

        for (var index = 0; index < 5; index++)
        {
            await TimeAsync(client, $"{TripClientExtensions.Trips}/plan", Body(index));
        }

        var samples = new List<double>();
        for (var index = 0; index < OutfitPerformanceTests.Samples; index++)
        {
            samples.Add(await TimeAsync(client, $"{TripClientExtensions.Trips}/plan", Body(index)));
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"trip (14 days, 150 pieces) p50 {Percentile(samples, 50):F1} ms  p95 {Percentile(samples, 95):F1} ms  max {samples.Max():F1} ms");
        Assert.True(Percentile(samples, 95) <= TripBudget.TotalMilliseconds, $"trip p95 {Percentile(samples, 95):F1} ms");
    }

    private static async Task<double> TimeAsync(HttpClient client, string url, object body)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.PostAsJsonAsync(url, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        stopwatch.Stop();

        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {text}");
        using var json = JsonDocument.Parse(text);
        var answered = json.RootElement.TryGetProperty("outfits", out var outfits)
            ? outfits.GetArrayLength() > 0
            : json.RootElement.GetProperty("coverage").GetProperty("covered").GetInt32() > 0;
        Assert.True(answered, $"{url} answered with nothing: {text[..Math.Min(400, text.Length)]}");

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(List<double> samples, int percentile)
    {
        var sorted = samples.Order().ToList();
        return sorted[Math.Max(0, (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1)];
    }
}
