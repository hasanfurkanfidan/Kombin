using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Kombin.Api.Common.Vision;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Bulk add ("Toplu ekle") answers of the fake, by the text of the photo (<see cref="Photo"/>), like the other flavours:
/// <list type="bullet">
/// <item><c>bulk-3</c> (and any other photo): three pieces in reading order, a t-shirt and a pair of jeans pre-ticked and a jacket that is not
/// (<c>cut_by_frame</c>); no unseparated garments;</item>
/// <item><c>bulk-refusal-&lt;code&gt;</c>: no pieces, that refusal (<c>closet</c>, <c>rail</c>, <c>pile</c>, <c>worn</c>, <c>nothing_found</c>, <c>too_dark</c>,
/// <c>too_blurry</c>; any other word is an unknown code, which the backend must keep);</item>
/// <item><c>bulk-busy</c>: the first call answers <c>503 busy</c> with <c>Retry-After: 5</c>, the next one is the <c>bulk-3</c> answer; <c>bulk-busy-twice</c>:
/// every call is busy; <c>bulk-unreachable</c>: a transient network failure;</item>
/// <item><c>bulk-4xx</c>: a permanent <c>400 unsupported_image</c>; <c>bulk-bad-embedding</c>: the second piece's embedding has 3 floats;
/// <c>bulk-bad-image</c>: the first piece's cutout is not base64;</item>
/// <item><c>bulk-many-&lt;n&gt;</c>: n pieces, all pre-ticked (more than the batch keeps, to see them cut off); <c>bulk-unseparated-&lt;n&gt;</c>: the
/// <c>bulk-3</c> pieces and n garments the service did not cut apart (one region).</item>
/// </list>
/// A piece's cutout, thumbnail and embedding are its own (<see cref="BulkCutout"/>, <see cref="EmbeddingFor"/> of the photo's text and the piece's
/// number), so staged copies and duplicate hints can tell them apart. Calls are counted per text (<see cref="BulkCalls"/>).
/// </summary>
internal sealed partial class FakeVisionClient
{
    private static readonly ConcurrentDictionary<string, int> BulkCounts = new(StringComparer.Ordinal);

    /// <summary>Bulk calls whose photo contained <paramref name="text"/>.</summary>
    public static int BulkCalls(string text) => BulkCounts.Where(entry => entry.Key.Contains(text, StringComparison.Ordinal)).Sum(entry => entry.Value);

    /// <summary>The cutout the fake answers for piece <paramref name="index"/> of the photo with this text: the PNG that must end up in the staged key.</summary>
    public static byte[] BulkCutout(string photoText, int index) => Encoding.UTF8.GetBytes($"bulk-cutout-png:{index}:{photoText}");

    /// <summary>The thumbnail of piece <paramref name="index"/> (WEBP bytes of the fake).</summary>
    public static byte[] BulkThumbnail(string photoText, int index) => Encoding.UTF8.GetBytes($"bulk-thumb-webp:{index}:{photoText}");

    private static readonly ConcurrentDictionary<string, Action> BulkHooks = new(StringComparer.Ordinal);

    /// <summary>Runs <paramref name="hook"/> (once per call) while the fake answers a bulk photo with this text, e.g. to end the batch meanwhile; null removes it.</summary>
    public static void OnBulk(string text, Action? hook)
    {
        if (hook is null)
        {
            BulkHooks.TryRemove(text, out _);
        }
        else
        {
            BulkHooks[text] = hook;
        }
    }

    public Task<VisionCall<VisionBulk>> BulkAsync(byte[] image, string contentType, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(image);
        var calls = BulkCounts.AddOrUpdate(text, 1, (_, count) => count + 1);
        if (BulkHooks.TryGetValue(text, out var hook))
        {
            hook();
        }

        if (text.Contains("bulk-unreachable", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionBulk>.Transient("vision_unreachable: connection refused"));
        }

        if (text.Contains("bulk-4xx", StringComparison.Ordinal))
        {
            return Task.FromResult(VisionCall<VisionBulk>.Answered(400, "unsupported_image", isTransient: false));
        }

        if (text.Contains("bulk-busy-twice", StringComparison.Ordinal) || (text.Contains("bulk-busy", StringComparison.Ordinal) && calls == 1))
        {
            return Task.FromResult(VisionCall<VisionBulk>.Answered(503, "busy", isTransient: true, retryAfter: TimeSpan.FromSeconds(5)));
        }

        var refusal = Flavor(text, "bulk-refusal-");
        if (refusal is not null)
        {
            return Task.FromResult(VisionCall<VisionBulk>.Ok(new VisionBulk(refusal, [], null, Stages)));
        }

        var count = Flavor(text, "bulk-many-") is { } many && int.TryParse(many, CultureInfo.InvariantCulture, out var parsed) ? parsed : 3;
        List<VisionBulkPiece> pieces = [];
        for (var index = 0; index < count; index++)
        {
            var preTicked = count != 3 || index < 2;
            pieces.Add(BulkPiece(text, index, preTicked));
        }

        if (text.Contains("bulk-bad-embedding", StringComparison.Ordinal))
        {
            pieces[1] = pieces[1] with { Analysis = pieces[1].Analysis with { Embedding = [1f, 0f, 0f] } };
        }

        if (text.Contains("bulk-bad-image", StringComparison.Ordinal))
        {
            pieces[0] = pieces[0] with { Analysis = pieces[0].Analysis with { Cutout = "not base64 !!" } };
        }

        var unseparated = Flavor(text, "bulk-unseparated-") is { } digits && int.TryParse(digits, CultureInfo.InvariantCulture, out var left) && left > 0
            ? new VisionUnseparated(left, [new VisionRegionBox(0.1, 0.2, 0.4, 0.3)])
            : null;

        return Task.FromResult(VisionCall<VisionBulk>.Ok(new VisionBulk(null, pieces, unseparated, Stages)));
    }

    private static readonly Dictionary<string, double> Stages = new() { ["detector"] = 600, ["analysis"] = 1700, ["total"] = 5300 };

    /// <summary>The digits or word right after <paramref name="prefix"/> in the photo's text (up to the next colon), or null when it is not there.</summary>
    private static string? Flavor(string text, string prefix)
    {
        var start = text.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var rest = text[(start + prefix.Length)..];
        var end = rest.IndexOfAny([':', ' ']);

        return end < 0 ? rest : rest[..end];
    }

    /// <summary>
    /// Piece 0 is a beige t-shirt, piece 1 blue jeans, any other a navy jacket; only the first two (and every piece of a <c>bulk-many</c> photo) are
    /// pre-ticked, the others carry <c>cut_by_frame</c>.
    /// </summary>
    private static VisionBulkPiece BulkPiece(string text, int index, bool preTicked)
    {
        var (slug, color, hex) = (index % 3) switch
        {
            0 => ("ust-giyim/tisort", "Beige", "#D8C3A5"),
            1 => ("alt-giyim/jean", "Blue", "#2F5D9C"),
            _ => ("ust-giyim/gomlek", "Navy", "#1F2A44"),
        };
        var analysis = new VisionAnalysis(
            Convert.ToBase64String(BulkCutout(text, index)),
            Convert.ToBase64String(BulkThumbnail(text, index)),
            new VisionCategory(slug, 0.9),
            [new VisionCategory(slug, 0.9), new VisionCategory("ust-giyim/triko", 0.06), new VisionCategory("ust-giyim/gomlek", 0.02)],
            new VisionColor(color, 0.8, hex),
            null,
            new VisionPattern("Solid", 0.95),
            EmbeddingFor($"{text}#{index}"),
            new VisionQuality(true, 0.9, []));

        return new VisionBulkPiece(
            new VisionRegionBox(0.05 + (0.3 * (index % 3)), 0.1 + (0.4 * (index / 3 % 2)), 0.28, 0.35),
            preTicked,
            preTicked ? [] : ["cut_by_frame"],
            analysis);
    }
}
