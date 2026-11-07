using System.Text;
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
/// </summary>
internal sealed class FakeVisionClient : IVisionClient
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

    public Task<VisionCall<VisionDetection>> DetectAsync(byte[] image, string contentType, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetString(image).Contains("no-detect", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionDetection>.Transient("vision_503: busy"));
        }

        VisionRegion Region(double y, string slot, string slug, string color) =>
            new(new VisionRegionBox(0.25, y, 0.5, 0.35), slot, new VisionCategory(slug, 0.8), [new VisionCategory(slug, 0.8)], new VisionColor(color, 0.7, null), 0.8);

        return Task.FromResult(VisionCall<VisionDetection>.Ok(new VisionDetection(
            "person",
            [Region(0.15, "Top", "ust-giyim/tisort", "Red"), Region(0.5, "Bottom", "alt-giyim/jean", "Blue")])));
    }
}
