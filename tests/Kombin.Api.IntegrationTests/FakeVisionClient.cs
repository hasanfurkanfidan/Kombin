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
}
