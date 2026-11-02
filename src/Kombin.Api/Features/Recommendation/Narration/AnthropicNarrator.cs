using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Recommendation.Narration;

internal sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>Env <c>Anthropic__ApiKey</c>. Without it the Anthropic narrator answers with the templates.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "claude-haiku-5-5";

    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public string ApiVersion { get; set; } = "2026-10-21";
}

/// <summary>
/// Narrates with Claude: one Messages API call per outfit set, at most <see cref="MaxTokensPerOutfit"/> output tokens per outfit, a
/// strict JSON schema for the answer (<c>output_config.format</c>), thinking off and low effort (it is one sentence), and a
/// <see cref="Timeout"/> budget. Lines are cached by summary hash in <c>recommendation.narrations</c> for <see cref="CacheLifetime"/>,
/// so an outfit is narrated once. Any failure (no key, HTTP error, timeout, refusal, truncation, malformed JSON, a line that is empty or
/// too long) falls back to <see cref="TemplateNarrator"/> for the outfits concerned; nothing is thrown. It runs in the
/// <c>outfit-narrate</c> job, never in a request.
/// </summary>
internal sealed partial class AnthropicNarrator(
    HttpClient http, KombinDbContext db, IClock clock, IOptions<AnthropicOptions> options, ILogger<AnthropicNarrator> logger) : IOutfitNarrator
{
    public const int MaxTokensPerOutfit = 60;

    /// <summary>A line longer than this is not one line; the template is used instead.</summary>
    public const int MaxLineLength = 200;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private const string SystemPrompt =
        "Sen zevkli bir arkadaşsın ve Kombin uygulamasında kombin önerilerine tek cümlelik bir gerekçe yazıyorsun. "
        + "Her kombin için doğal, samimi (sen diliyle) tek bir Türkçe cümle yaz: neden işe yaradığını, verilen kurala (rule) dayanarak söyle, "
        + "en fazla iki parçadan bahset. En fazla 140 karakter. Emoji, hashtag, fiyat ve marka yok. Hava ve etkinlik verilmişse onlara uygun konuş. "
        + "Her id için bir satır döndür.";

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["lines"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["text"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("id", "text"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("lines"),
        ["additionalProperties"] = false,
    };

    public static void Configure(HttpClient client) => client.Timeout = Timeout;

    public async Task<IReadOnlyList<OutfitLine>> NarrateAsync(IReadOnlyList<OutfitSummary> outfits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outfits);

        var hashes = outfits.Select(outfit => outfit.Hash).ToList();
        var lines = outfits.Select(outfit => new OutfitLine(TemplateNarrator.Narrate(outfit), NarrationProvider.Template)).ToArray();
        var cached = await NarrationCache.LoadAsync(db, hashes, clock.UtcNow, cancellationToken);

        var misses = new List<int>();
        for (var index = 0; index < outfits.Count; index++)
        {
            if (cached.TryGetValue(hashes[index], out var text))
            {
                lines[index] = new OutfitLine(text, NarrationProvider.Anthropic);
            }
            else
            {
                misses.Add(index);
            }
        }

        if (misses.Count == 0 || string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            return lines;
        }

        var written = await CallAsync([.. misses.Select(index => outfits[index])], cancellationToken);
        if (written is null)
        {
            return lines;
        }

        var now = clock.UtcNow;
        for (var position = 0; position < misses.Count; position++)
        {
            if (written.TryGetValue(position.ToString(System.Globalization.CultureInfo.InvariantCulture), out var text) && IsUsable(text))
            {
                var index = misses[position];
                lines[index] = new OutfitLine(text.Trim(), NarrationProvider.Anthropic);
                await NarrationCache.StoreAsync(db, hashes[index], text.Trim(), options.Value.Model, now, cancellationToken);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // A parallel run cached the same outfit first; its line is as good as ours.
            db.ChangeTracker.Clear();
        }

        return lines;
    }

    /// <summary>The Messages API request for the outfits; their position in the list is their <c>id</c>.</summary>
    internal static JsonObject BuildRequest(IReadOnlyList<OutfitSummary> outfits, string model)
    {
        ArgumentNullException.ThrowIfNull(outfits);

        var input = new JsonArray([.. outfits.Select((outfit, index) => (JsonNode)Describe(outfit, index))]);

        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokensPerOutfit * outfits.Count,
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["output_config"] = new JsonObject
            {
                ["effort"] = "low",
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = Schema.DeepClone() },
            },
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = input.ToJsonString() }),
        };
    }

    /// <summary>The lines by id, or null when the answer is not a complete, schema-shaped message.</summary>
    internal static Dictionary<string, string>? ParseResponse(string body)
    {
        try
        {
            var message = JsonNode.Parse(body);
            if (message?["stop_reason"]?.GetValue<string>() != "end_turn")
            {
                return null;
            }

            var text = message["content"]?.AsArray()
                .FirstOrDefault(block => block?["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>();
            var answer = text is null ? null : JsonNode.Parse(text);

            return answer?["lines"]?.AsArray()
                .Where(line => line?["id"] is not null && line["text"] is not null)
                .GroupBy(line => line!["id"]!.GetValue<string>(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First()!["text"]!.GetValue<string>(), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool IsUsable(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= MaxLineLength && !text.Contains('\n', StringComparison.Ordinal);

    private static JsonObject Describe(OutfitSummary outfit, int index) => new()
    {
        ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["kind"] = outfit.Kind.ToString(),
        ["rule"] = outfit.Rule.ToString(),
        ["occasion"] = outfit.Occasion?.ToString(),
        ["weather"] = outfit.Band is null && !outfit.Rain
            ? null
            : new JsonObject { ["band"] = outfit.Band?.ToString(), ["rain"] = outfit.Rain, ["temperatureC"] = outfit.TemperatureC },
        ["pieces"] = new JsonArray([.. outfit.Pieces.Select(piece => (JsonNode)new JsonObject
        {
            ["slot"] = piece.Slot.ToString(),
            ["garment"] = TurkishGrammar.Noun(piece.CategorySlug),
            ["color"] = TurkishGrammar.Colour(piece.Color),
            ["fit"] = piece.Fit.ToString(),
            ["pattern"] = piece.Pattern.ToString(),
            ["fromCatalog"] = piece.IsGap,
            ["anchor"] = piece.IsAnchor,
        })]),
    };

    private async Task<Dictionary<string, string>?> CallAsync(IReadOnlyList<OutfitSummary> outfits, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(BuildRequest(outfits, settings.Model).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", settings.ApiKey);
        request.Headers.Add("anthropic-version", settings.ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                LogCallFailed(logger, (int)response.StatusCode, outfits.Count);
                return null;
            }

            var lines = ParseResponse(body);
            if (lines is null)
            {
                LogUnusableAnswer(logger, outfits.Count);
            }

            return lines;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            LogCallError(logger, exception, outfits.Count);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Anthropic narrator got HTTP {Status} for {Count} outfits; templates are kept.")]
    private static partial void LogCallFailed(ILogger logger, int status, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Anthropic narrator's answer for {Count} outfits was not usable; templates are kept.")]
    private static partial void LogUnusableAnswer(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Anthropic narrator failed for {Count} outfits; templates are kept.")]
    private static partial void LogCallError(ILogger logger, Exception exception, int count);
}

/// <summary>The LLM lines in <c>recommendation.narrations</c>, read by the outfit endpoints (one query) and written by the narrator.</summary>
internal static class NarrationCache
{
    public static async Task<Dictionary<string, string>> LoadAsync(KombinDbContext db, IReadOnlyCollection<string> hashes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (hashes.Count == 0)
        {
            return [];
        }

        var since = now - AnthropicNarrator.CacheLifetime;

        return await db.OutfitNarrations.AsNoTracking()
            .Where(narration => hashes.Contains(narration.Hash) && narration.CreatedAt >= since)
            .ToDictionaryAsync(narration => narration.Hash, narration => narration.Text, StringComparer.Ordinal, cancellationToken);
    }

    /// <summary>Adds or refreshes the line (an expired row is overwritten); the caller saves.</summary>
    public static async Task StoreAsync(KombinDbContext db, string hash, string text, string model, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.OutfitNarrations.FindAsync([hash], cancellationToken);
        if (row is null)
        {
            db.OutfitNarrations.Add(new OutfitNarration { Hash = hash, Text = text, Model = model, CreatedAt = now });
            return;
        }

        row.Text = text;
        row.Model = model;
        row.CreatedAt = now;
    }
}
