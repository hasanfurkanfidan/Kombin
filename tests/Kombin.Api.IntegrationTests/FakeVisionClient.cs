using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kombin.Api.Features.Wardrobe;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The vision service for the tests. Like <see cref="FakeExpoPushClient"/> it keeps no per-test script: the answer depends only on the
/// photo, whose bytes are a text the test chooses (see <see cref="Photo"/>).
/// <list type="bullet">
/// <item><c>unreachable</c>: a transient failure, as if the service were down.</item>
/// <item><c>refused</c>: a permanent <c>400 unsupported_image</c>.</item>
/// <item><c>unsure</c>: a t-shirt at 0.20 confidence, below the threshold.</item>
/// <item><c>not-garment</c>: <c>isGarment=false</c> with a <c>person_wearing</c> warning.</item>
/// <item>anything else: a beige t-shirt at 0.92 with a white secondary color.</item>
/// </list>
/// Moderation and detection (social posts) answer by the same text: <c>nsfw</c> blocks, <c>borderline</c> needs review, <c>unreachable</c>
/// and <c>refused</c> fail as above, <c>no-detect</c> moderates fine but detection fails; anything else is allowed, with two proposals
/// (a red t-shirt and blue jeans). <see cref="PostPhoto"/> makes a structurally valid JPEG carrying the text and a GPS EXIF block.
/// <para>
/// Visual search: <c>/v1/embed</c> answers <see cref="EmbeddingFor"/> of the image's text (<c>unreachable</c> and <c>refused</c> fail as above), and a
/// detection of a <see cref="LensPhoto"/> answers the regions that photo spells out, each with that embedding. Texts that share a
/// <c>look-&lt;key&gt;</c> marker get near vectors (see <see cref="EmbeddingFor"/>), any other two are unrelated.
/// </para>
/// </summary>
internal sealed partial class FakeVisionClient : IVisionClient
{
    public const string Model = "ViT-B-32/laion2b_s34b_b79k";

    public static readonly byte[] Cutout = Encoding.UTF8.GetBytes("cutout-png-bytes");

    public static readonly byte[] Thumbnail = Encoding.UTF8.GetBytes("thumb-webp-bytes");

    /// <summary>A unique "photo" whose <paramref name="flavor"/> tells the fake how to answer.</summary>
    public static byte[] Photo(string flavor = "ok") => Encoding.UTF8.GetBytes($"photo:{flavor}:{Guid.NewGuid():N}");

    public Task<VisionResult> AnalyzeAsync(byte[] image, string contentType, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(image);

        if (text.Contains("unreachable", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionResult.Transient("vision_unreachable: connection refused"));
        }

        if (text.Contains("refused", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionResult.Permanent("vision_400: unsupported_image"));
        }

        var unsure = text.Contains("unsure", StringComparison.Ordinal);
        var notGarment = text.Contains("not-garment", StringComparison.Ordinal);
        VisionCategory[] candidates = unsure
            ? [new("ust-giyim/tisort", 0.20), new("ust-giyim/gomlek", 0.18), new("ust-giyim/triko", 0.15)]
            : [new("ust-giyim/tisort", 0.92), new("ust-giyim/gomlek", 0.05), new("ust-giyim/triko", 0.01)];

        var embedding = new float[512];
        embedding[0] = 1f;

        return Task.FromResult(VisionResult.Ok(new VisionAnalysis(
            Convert.ToBase64String(Cutout),
            Convert.ToBase64String(Thumbnail),
            candidates[0],
            candidates,
            new VisionColor("Beige", 0.78, "#D8C3A5"),
            new VisionColor("White", 0.22, "#F4F4F4"),
            new VisionPattern("Solid", 0.95),
            embedding,
            new VisionQuality(!notGarment, 0.88, notGarment ? ["person_wearing"] : []))));
    }

    public Task<string?> GetEmbeddingModelAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Model);

    /// <summary>What the stored feed image and preview contain after processing.</summary>
    public static readonly byte[] FeedImage = Encoding.UTF8.GetBytes("feed-jpeg-bytes");

    public static readonly byte[] Preview = Encoding.UTF8.GetBytes("preview-webp-bytes");

    /// <summary>The GPS text inside <see cref="PostPhoto"/>'s EXIF block, which processing must remove.</summary>
    public const string GpsMarker = "GPS-41.0082N-28.9784E";

    /// <summary>
    /// A post photo: SOI, JFIF, an EXIF block with a GPS marker and orientation 6, a comment, a scan whose data is the flavor text, EOI.
    /// Not decodable, but the metadata stripper reads its structure like any JPEG.
    /// </summary>
    public static byte[] PostPhoto(string flavor = "ok")
    {
        static byte[] Segment(byte marker, byte[] data) => [0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)((data.Length + 2) & 0xFF), .. data];

        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        byte[] exif = [.. "Exif\0\0"u8, .. tiff, .. Encoding.ASCII.GetBytes(GpsMarker)];
        byte[] scan = [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00];

        return
        [
            0xFF, 0xD8,
            .. Segment(0xE0, [.. "JFIF\0"u8, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]),
            .. Segment(0xE1, exif),
            .. Segment(0xFE, Encoding.ASCII.GetBytes("taken at home")),
            .. Segment(0xDA, scan),
            .. Encoding.UTF8.GetBytes($"photo:{flavor}:{Guid.NewGuid():N}"),
            0xFF, 0xD9,
        ];
    }

    public Task<VisionCall<VisionModeration>> ModerateAsync(byte[] image, string contentType, bool renditions, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(image);

        if (text.Contains("unreachable", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionModeration>.Transient("vision_unreachable: connection refused"));
        }

        if (text.Contains("refused", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionModeration>.Permanent("vision_400: unsupported_image"));
        }

        var (verdict, labels) = text.Contains("nsfw", StringComparison.Ordinal)
            ? ("block", new[] { "nsfw" })
            : text.Contains("borderline", StringComparison.Ordinal) ? ("review", new[] { "nudity" }) : ("allow", Array.Empty<string>());

        return Task.FromResult(VisionCall<VisionModeration>.Ok(new VisionModeration(
            verdict != "block",
            verdict == "review",
            verdict,
            labels,
            new Dictionary<string, double> { ["nsfw"] = verdict == "block" ? 0.9 : 0.01, ["nudity"] = verdict == "review" ? 0.3 : 0.01, ["violence"] = 0.0, ["not_person_or_garment"] = 0.02 },
            renditions ? new VisionRenditions(Convert.ToBase64String(FeedImage), 1200, 1600, Convert.ToBase64String(Preview)) : null)));
    }

    public Task<VisionCall<VisionDetection>> DetectAsync(byte[] image, string contentType, bool embed, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(image);
        if (text.Contains("no-detect", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionDetection>.Transient("vision_503: busy"));
        }

        if (text.StartsWith(LensPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(DetectLens(text, embed));
        }

        VisionRegion Region(double y, string slot, string slug, string color) =>
            new(new VisionRegionBox(0.25, y, 0.5, 0.35), slot, new VisionCategory(slug, 0.8), [new VisionCategory(slug, 0.8)], new VisionColor(color, 0.7, null), 0.8);

        return Task.FromResult(VisionCall<VisionDetection>.Ok(new VisionDetection(
            "person",
            [Region(0.15, "Top", "ust-giyim/tisort", "Red"), Region(0.5, "Bottom", "alt-giyim/jean", "Blue")])));
    }

    public Task<VisionCall<VisionEmbedding>> EmbedAsync(byte[] image, string contentType, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(image);

        if (text.Contains("unreachable", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionEmbedding>.Transient("vision_unreachable: connection refused"));
        }

        if (text.Contains("refused", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionEmbedding>.Permanent("vision_400: unsupported_image"));
        }

        return Task.FromResult(VisionCall<VisionEmbedding>.Ok(new VisionEmbedding(EmbeddingFor(text))));
    }

    private const string LensPrefix = "lens|";

    /// <summary>
    /// A query photo for <c>/lens/search</c>: <paramref name="embeddingText"/> is what its regions embed as (pass the catalog image's bytes as
    /// text, <c>FakeCatalogImageHandler.BytesFor(url)</c>, to photograph that very product); each region is given by its candidates, best first
    /// (slug and confidence). No region: the photo is only embedded as a whole.
    /// </summary>
    public static byte[] LensPhoto(string embeddingText, params (string Slug, double Confidence)[][] regions) =>
        Encoding.UTF8.GetBytes(LensPrefix
            + string.Join(';', regions.Select(region => string.Join(',', region.Select(candidate => $"{candidate.Slug}={candidate.Confidence.ToString(CultureInfo.InvariantCulture)}"))))
            + "|" + embeddingText);

    /// <summary>One confident region of the given category.</summary>
    public static byte[] LensPhoto(string embeddingText, string categorySlug) => LensPhoto(embeddingText, [(categorySlug, 0.9)]);

    private static VisionCall<VisionDetection> DetectLens(string text, bool embed)
    {
        var parts = text.Split('|', 3);
        var embedding = EmbeddingFor(parts[2]);
        var regions = parts[1].Length == 0
            ? []
            : parts[1].Split(';').Select((region, index) =>
            {
                var candidates = region.Split(',')
                    .Select(candidate => candidate.Split('='))
                    .Select(pair => new VisionCategory(pair[0], double.Parse(pair[1], CultureInfo.InvariantCulture)))
                    .ToList();
                var slot = candidates[0].Slug.Split('/')[0] switch
                {
                    "ust-giyim" => "Top",
                    "alt-giyim" => "Bottom",
                    "dis-giyim" => "Outerwear",
                    "ayakkabi" => "Shoes",
                    _ => "Accessory",
                };

                return new VisionRegion(
                    new VisionRegionBox(0.2, 0.1 + (0.3 * index), 0.6, 0.3),
                    slot,
                    candidates[0],
                    candidates,
                    new VisionColor("Navy", 0.7, "#1F2A44"),
                    candidates[0].Confidence,
                    embed ? embedding : null);
            }).ToList();

        return VisionCall<VisionDetection>.Ok(new VisionDetection("flatlay", regions, embed ? embedding : null));
    }

    /// <summary>
    /// A deterministic unit vector for a text. A text with a <c>look-&lt;key&gt;</c> marker is that look's vector plus a little of its own
    /// (weight 0.25, or 0.6 when the text also says <c>loose</c>): two tight members of a look are ~0.94 alike, a loose and a tight one
    /// ~0.83, unrelated texts ~0.
    /// </summary>
    public static float[] EmbeddingFor(string text)
    {
        var own = RandomVector(text);
        var look = LookMarker().Match(text);
        if (!look.Success)
        {
            return own;
        }

        var weight = text.Contains("loose", StringComparison.Ordinal) ? 0.6f : 0.25f;
        var shared = RandomVector(look.Value);
        var mixed = shared.Select((value, index) => value + (weight * own[index])).ToArray();

        return Normalize(mixed);
    }

    private static float[] RandomVector(string seed)
    {
        var random = new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(seed)), 0));
        var vector = new float[512];
        for (var index = 0; index < vector.Length; index++)
        {
            // Box-Muller: Gaussian components make random directions uniform on the sphere.
            vector[index] = (float)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()));
        }

        return Normalize(vector);
    }

    private static float[] Normalize(float[] vector)
    {
        var length = (float)Math.Sqrt(vector.Sum(value => (double)value * value));
        return [.. vector.Select(value => value / length)];
    }

    [GeneratedRegex("look-[a-z0-9]+")]
    private static partial Regex LookMarker();
}
