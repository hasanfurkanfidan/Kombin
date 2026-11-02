using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Recommendation.Jobs;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kombin.Api.IntegrationTests;

/// <summary>The Anthropic narrator against a fake Messages API, with the real narration cache in PostgreSQL.</summary>
public sealed class AnthropicNarratorTests(ApiFactory factory)
{
    private const string ApiKey = "test-anthropic-key";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Unique per call: the cache is shared by every test, so each test narrates outfits nobody else has.</summary>
    private static OutfitSummary NewSummary(OutfitRule rule = OutfitRule.OneStatement) =>
        new(
            OutfitKind.Daily,
            rule,
            null,
            null,
            false,
            null,
            [
                new(OutfitSlot.Top, $"ust-giyim/test-{Guid.NewGuid():N}", ProductColor.Red, ProductFit.Regular, ProductPattern.Solid, false, false),
                new(OutfitSlot.Bottom, "alt-giyim/pantolon", ProductColor.Navy, ProductFit.Regular, ProductPattern.Solid, false, false),
                new(OutfitSlot.Shoes, "ayakkabi/sneaker", ProductColor.White, ProductFit.Regular, ProductPattern.Solid, false, false),
            ]);

    private static string Answer(params string[] lines) =>
        new JsonObject
        {
            ["id"] = "msg_test",
            ["type"] = "message",
            ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = new JsonObject { ["lines"] = new JsonArray([.. lines.Select((line, index) => (JsonNode)new JsonObject { ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["text"] = line })]) }.ToJsonString(),
            }),
        }.ToJsonString();

    private async Task<T> WithNarratorAsync<T>(FakeAnthropicHandler handler, Func<AnthropicNarrator, KombinDbContext, Task<T>> action, string apiKey = ApiKey)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        using var http = new HttpClient(handler) { Timeout = AnthropicNarrator.Timeout };
        var narrator = new AnthropicNarrator(
            http,
            db,
            scope.ServiceProvider.GetRequiredService<IClock>(),
            Options.Create(new AnthropicOptions { ApiKey = apiKey, BaseUrl = "https://anthropic.test" }),
            NullLogger<AnthropicNarrator>.Instance);

        return await action(narrator, db);
    }

    [Fact]
    public async Task OneCallNarratesTheSetWithTheJsonContractAndTheCacheAnswersNextTime()
    {
        var handler = new FakeAnthropicHandler((_, _) => Task.FromResult(FakeAnthropicHandler.Json(Answer("Kırmızı tişört sahnede, lacivert sakin.", "Beyaz sneaker hafiflik katıyor."))));
        var summaries = new[] { NewSummary(), NewSummary(OutfitRule.AllNeutral) };

        var lines = await WithNarratorAsync(handler, (narrator, _) => narrator.NarrateAsync(summaries, Ct));

        Assert.Equal(["Kırmızı tişört sahnede, lacivert sakin.", "Beyaz sneaker hafiflik katıyor."], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.Equal(NarrationProvider.Anthropic, line.Provider));

        var call = Assert.Single(handler.Calls);
        Assert.Equal("https://anthropic.test/v1/messages", call.Url);
        Assert.Equal(ApiKey, call.Headers["x-api-key"]);
        Assert.True(call.Headers.ContainsKey("anthropic-version"));
        var body = JsonNode.Parse(call.Body)!;
        Assert.Equal("claude-haiku-5-5", body["model"]!.GetValue<string>());
        Assert.Equal(2 * AnthropicNarrator.MaxTokensPerOutfit, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());

        // Cached by summary hash: the same outfits are not sent again.
        var again = await WithNarratorAsync(handler, (narrator, _) => narrator.NarrateAsync(summaries, Ct));
        Assert.Equal(lines.Select(line => line.Text), again.Select(line => line.Text));
        Assert.Single(handler.Calls);

        var stored = await factory.WithDbAsync(db => db.OutfitNarrations.AsNoTracking().Where(row => row.Hash == summaries[0].Hash).SingleAsync(Ct));
        Assert.Equal("claude-haiku-5-5", stored.Model);
    }

    [Fact]
    public async Task ATimeoutFallsBackToTheTemplatesWithinTheBudget()
    {
        var handler = new FakeAnthropicHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return FakeAnthropicHandler.Json(Answer("too late"));
        });
        var summary = NewSummary();
        var stopwatch = Stopwatch.StartNew();

        var lines = await WithNarratorAsync(handler, (narrator, _) => narrator.NarrateAsync([summary], Ct));

        stopwatch.Stop();
        Assert.Equal(TemplateNarrator.Narrate(summary), Assert.Single(lines).Text);
        Assert.Equal(NarrationProvider.Template, lines[0].Provider);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), $"took {stopwatch.Elapsed}");
        Assert.False(await factory.WithDbAsync(db => db.OutfitNarrations.AnyAsync(row => row.Hash == summary.Hash, Ct)));
    }

    [Theory]
    [InlineData(500, "{\"type\":\"error\"}")]
    [InlineData(200, "{\"stop_reason\":\"max_tokens\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"lines\\\":[\"}]}")]
    [InlineData(200, "not json at all")]
    public async Task AFailedOrUnusableAnswerFallsBackToTheTemplates(int status, string body)
    {
        var handler = new FakeAnthropicHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var summary = NewSummary();

        var lines = await WithNarratorAsync(handler, (narrator, _) => narrator.NarrateAsync([summary], Ct));

        Assert.Equal(new OutfitLine(TemplateNarrator.Narrate(summary), NarrationProvider.Template), Assert.Single(lines));
    }

    [Fact]
    public async Task OnlyTheUnusableLineFallsBack()
    {
        var handler = new FakeAnthropicHandler((_, _) => Task.FromResult(FakeAnthropicHandler.Json(Answer("İyi bir satır.", new string('x', 500)))));
        var summaries = new[] { NewSummary(), NewSummary() };

        var lines = await WithNarratorAsync(handler, (narrator, _) => narrator.NarrateAsync(summaries, Ct));

        Assert.Equal(new OutfitLine("İyi bir satır.", NarrationProvider.Anthropic), lines[0]);
        Assert.Equal(new OutfitLine(TemplateNarrator.Narrate(summaries[1]), NarrationProvider.Template), lines[1]);
    }

    [Fact]
    public async Task WithoutAKeyNothingIsCalled()
    {
        var handler = new FakeAnthropicHandler((_, _) => Task.FromResult(FakeAnthropicHandler.Json(Answer("x"))));

        var lines = await WithNarratorAsync(handler, (narrator, _) => narrator.NarrateAsync([NewSummary()], Ct), apiKey: string.Empty);

        Assert.Equal(NarrationProvider.Template, Assert.Single(lines).Provider);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task TheBackgroundJobUpgradesTheStoredOutfitsLine()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.InsertWardrobeAsync(user.Id, [new("tee", "ust-giyim/tisort", ProductColor.White, 2), new("jeans", "alt-giyim/jean", ProductColor.Blue, 2), new("shoes", "ayakkabi/sneaker", ProductColor.White, 2)], Ct);
        var outfit = (await user.Client.DailyAsync(cancellationToken: Ct)).GetProperty("outfit");
        var id = outfit.GetProperty("id").GetGuid();
        Assert.Equal("Template", outfit.GetProperty("narration").GetProperty("provider").GetString());

        var handler = new FakeAnthropicHandler((_, _) => Task.FromResult(FakeAnthropicHandler.Json(Answer("Sade, rahat ve her yere uyar."))));
        var upgraded = await WithNarratorAsync(handler, (narrator, db) => new OutfitNarrationJob(db, narrator).NarrateAsync([id], Ct));

        Assert.Equal(1, upgraded);
        var stored = await user.Client.GetJsonAsync($"{RecommendationClientExtensions.Base}/{id}", Ct);
        Assert.Equal("Sade, rahat ve her yere uyar.", stored.GetProperty("narration").GetProperty("text").GetString());
        Assert.Equal("Anthropic", stored.GetProperty("narration").GetProperty("provider").GetString());

        // Idempotent: an outfit already narrated by the LLM is skipped.
        Assert.Equal(0, await WithNarratorAsync(handler, (narrator, db) => new OutfitNarrationJob(db, narrator).NarrateAsync([id], Ct)));
        Assert.Single(handler.Calls);
    }
}

/// <summary>Answers Messages API calls with the test's function and records what was sent.</summary>
internal sealed class FakeAnthropicHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    public List<(string Url, Dictionary<string, string> Headers, string Body)> Calls { get; } = [];

    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.RequestUri!.ToString(), request.Headers.ToDictionary(header => header.Key, header => string.Join(',', header.Value), StringComparer.OrdinalIgnoreCase), body));

        return await answer(request, cancellationToken);
    }
}
