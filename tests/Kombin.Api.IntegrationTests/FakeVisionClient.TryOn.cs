using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Kombin.Api.Common.Vision;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Try-on ("Manken üstünde gör") answers of the fake, by the text of the photos (a wardrobe photo's flavour reaches its cutout through
/// <see cref="CutoutFor"/>; a catalogue photo's text is its URL, <see cref="FakeCatalogImageHandler.BytesFor"/>):
/// <list type="bullet">
/// <item><c>tryon-unreliable</c>: <c>reliable: false</c> (<c>worn_photo</c>); <c>tryon-folded</c>: a top with <c>mayBeOutermost: false</c>;</item>
/// <item><c>tryon-503</c>: prepare answers <c>503 busy</c>; <c>tryon-413</c>: <c>413 image_too_large</c> (permanent);</item>
/// <item>render: a garment whose cutout says <c>tryon-r422-&lt;code&gt;</c> is refused with <c>422 &lt;code&gt;</c>, <c>tryon-r503</c> answers
/// <c>503 busy</c>; a body over BMI 36 is <c>422 body_out_of_range</c> (the service's safety net).</item>
/// </list>
/// The family comes from the category slug like the service's. Calls are counted by any text a test chooses (<see cref="PrepareCalls"/>,
/// <see cref="RenderCalls"/>), the last render spec is kept per text (<see cref="LastRenderSpec"/>), and <see cref="HoldRender"/> parks a render.
/// </summary>
internal sealed partial class FakeVisionClient
{
    public const string Renderer = "mannequin-v5";

    public const string PrepareVersion = "prepare-v6";

    public static readonly byte[] RenderImage = Encoding.UTF8.GetBytes("render-webp-bytes");

    private static readonly ConcurrentQueue<string> PreparedTexts = new();

    private static readonly ConcurrentQueue<string> RenderedTexts = new();

    private static readonly ConcurrentQueue<(string Garments, string Spec)> Specs = new();

    private static readonly ConcurrentDictionary<string, ModerationHold> RenderHolds = new(StringComparer.Ordinal);

    /// <summary>Prepare calls whose photos contained <paramref name="text"/>.</summary>
    public static int PrepareCalls(string text) => PreparedTexts.Count(sent => sent.Contains(text, StringComparison.Ordinal));

    /// <summary>Render calls whose garments contained <paramref name="text"/>.</summary>
    public static int RenderCalls(string text) => RenderedTexts.Count(sent => sent.Contains(text, StringComparison.Ordinal));

    /// <summary>The spec of the last render whose garments contained <paramref name="text"/>.</summary>
    public static JsonElement LastRenderSpec(string text) =>
        JsonDocument.Parse(Specs.Last(sent => sent.Garments.Contains(text, StringComparison.Ordinal)).Spec).RootElement.Clone();

    /// <summary>Parks render calls whose garments contain <paramref name="text"/> until <see cref="ModerationHold.Release"/>.</summary>
    public static ModerationHold HoldRender(string text) => RenderHolds.GetOrAdd(text, _ => new ModerationHold());

    public static void ReleaseRender(string text)
    {
        if (RenderHolds.TryRemove(text, out var hold))
        {
            hold.Release();
        }
    }

    public Task<string?> GetRendererAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Renderer);

    public Task<VisionCall<VisionPrepared>> PrepareAsync(IReadOnlyList<VisionImage> images, string categorySlug, CancellationToken cancellationToken)
    {
        var texts = images.Select(image => Encoding.UTF8.GetString(image.Content)).ToList();
        var all = string.Join('|', texts);
        PreparedTexts.Enqueue(all);

        if (all.Contains("tryon-503", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionPrepared>.Answered(503, "busy", true));
        }

        if (all.Contains("tryon-413", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionPrepared>.Answered(413, "image_too_large", false));
        }

        var family = categorySlug switch
        {
            "ust-giyim/blazer" => "outerwear",
            _ when categorySlug.StartsWith("ust-giyim/", StringComparison.Ordinal) => "top",
            _ when categorySlug.StartsWith("dis-giyim/", StringComparison.Ordinal) => "outerwear",
            "alt-giyim/etek" => "skirt",
            _ when categorySlug.StartsWith("alt-giyim/", StringComparison.Ordinal) => "trousers",
            _ when categorySlug.StartsWith("ayakkabi/", StringComparison.Ordinal) => "shoes",
            "aksesuar/kemer" => "belt",
            _ => "bag",
        };

        // The last photo is the packshot (brands put it last), like the service's pick.
        var used = texts.Count - 1;
        var unreliable = texts[used].Contains("tryon-unreliable", StringComparison.Ordinal) || family is "bag" or "belt";
        var folded = texts[used].Contains("tryon-folded", StringComparison.Ordinal);
        using var landmarks = JsonDocument.Parse("""{"neck":{"x":0.5,"y":0.04},"hem_l":{"x":0.2,"y":0.95}}""");
        using var sleeves = JsonDocument.Parse(folded ? """{"left":"folded","right":"folded"}""" : """{"left":"hanging","right":"hanging"}""");

        return Task.FromResult(VisionCall<VisionPrepared>.Ok(new VisionPrepared(
            PrepareVersion,
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"prepared:{texts[used]}")),
            612,
            1024,
            family,
            unreliable ? "model" : "flat",
            used,
            !unreliable,
            unreliable ? ["worn_photo"] : [],
            landmarks.RootElement.Clone(),
            family is "top" or "outerwear" ? sleeves.RootElement.Clone() : null,
            family == "top" ? !folded : family == "outerwear" ? true : null)));
    }

    public async Task<VisionCall<VisionRender>> RenderAsync(string spec, IReadOnlyList<byte[]> garments, CancellationToken cancellationToken)
    {
        var texts = garments.Select(garment => Encoding.UTF8.GetString(garment)).ToList();
        var all = string.Join('|', texts);
        RenderedTexts.Enqueue(all);
        Specs.Enqueue((all, spec));

        var hold = RenderHolds.FirstOrDefault(pair => all.Contains(pair.Key, StringComparison.Ordinal)).Value;
        if (hold is not null)
        {
            hold.Entered.TrySetResult();
            await hold.Released.Task.WaitAsync(cancellationToken);
        }

        var refused = texts.Select(text => RefusalCode().Match(text)).FirstOrDefault(match => match.Success);
        if (refused is not null)
        {
            return VisionCall<VisionRender>.Answered(422, refused.Groups[1].Value, false);
        }

        if (all.Contains("tryon-r503", StringComparison.Ordinal))
        {
            return VisionCall<VisionRender>.Answered(503, "busy", true);
        }

        using var document = JsonDocument.Parse(spec);
        var body = document.RootElement.GetProperty("body");
        if (body.GetProperty("weightKg").ValueKind == JsonValueKind.Number)
        {
            var height = body.GetProperty("heightCm").ValueKind == JsonValueKind.Number ? body.GetProperty("heightCm").GetDouble() : 171;
            if (body.GetProperty("weightKg").GetDouble() / Math.Pow(height / 100, 2) > 36)
            {
                return VisionCall<VisionRender>.Answered(422, "body_out_of_range", false);
            }
        }

        var specGarments = document.RootElement.GetProperty("garments").EnumerateArray().ToList();
        if (specGarments.Count != garments.Count)
        {
            return VisionCall<VisionRender>.Answered(422, "garment_count_mismatch", false);
        }

        var defaulted = Circumferences.Where(name => body.GetProperty($"{name}Cm").ValueKind == JsonValueKind.Null).Append("shoulder").ToList();
        var width = document.RootElement.GetProperty("width").GetInt32();

        return VisionCall<VisionRender>.Ok(new VisionRender(
            Renderer,
            Convert.ToBase64String(RenderImage),
            "webp",
            width,
            width * 2,
            [.. specGarments.Select((garment, index) => new VisionRenderedGarment(index, FitLevel(garment), "size", "fitted", []))],
            defaulted));
    }

    private static readonly string[] SizeOrder = ["XS", "S", "M", "L", "XL", "XXL"];

    private static readonly string[] Circumferences = ["chest", "waist", "hip"];

    /// <summary>The service's rule in small: no size or no reference → null; the same → TrueToSize; smaller → Tight; larger → Loose.</summary>
    private static string? FitLevel(JsonElement garment)
    {
        var size = garment.GetProperty("size").ValueKind == JsonValueKind.String ? garment.GetProperty("size").GetString() : null;
        var reference = garment.GetProperty("referenceSize").ValueKind == JsonValueKind.String ? garment.GetProperty("referenceSize").GetString() : null;
        if (size is null || reference is null)
        {
            return null;
        }

        var difference = Array.IndexOf(SizeOrder, size) - Array.IndexOf(SizeOrder, reference);

        return difference == 0 ? "TrueToSize" : difference < 0 ? "Tight" : "Loose";
    }

    [System.Text.RegularExpressions.GeneratedRegex("tryon-r422-([a-z_]+)")]
    private static partial System.Text.RegularExpressions.Regex RefusalCode();
}
