using SkiaSharp;

namespace Kombin.Api.Common.Imaging;

/// <summary>One photo ready to send: <c>SourceIndex</c> is its position among the photos given (the product gallery's order).</summary>
internal sealed record FittedPhoto(int SourceIndex, byte[] Content, string ContentType, bool Scaled);

/// <summary>
/// Makes photos fit the vision service's size limits without losing anything it would use (try-on brief, "Photo size"): a photo over the
/// per-photo limit is scaled to at most <c>maxSide</c> on its long side, re-encoded as PNG when it has transparency (a packshot cut out by the
/// retailer keeps its alpha) and as JPEG 90 otherwise; the service works at 1600 px anyway. SkiaSharp (MIT) does the decoding and encoding.
/// A photo is never decoded when its header says it has more than <c>maxPixels</c> pixels (a decompression bomb costs a header read).
/// </summary>
internal static class VisionImageFit
{
    /// <summary>Above this a photo is not decoded at all: 60 MP is about 240 MB of pixels.</summary>
    public const long DefaultMaxPixels = 60_000_000;

    private const int JpegQuality = 90;

    /// <summary>How many times a photo that is still too large after scaling is scaled again, each time to 80 % of the side.</summary>
    private const int ShrinkSteps = 5;

    /// <summary>
    /// The photos of one request: each one over <paramref name="maxPhotoBytes"/> scaled; then, while the request is over
    /// <paramref name="maxRequestBytes"/>, the largest photo not scaled yet is scaled too, and when none is left the largest one is left out (never
    /// the last remaining one). A photo that cannot be decoded, or is too large in pixels, is left out when it is over the limit (a small one
    /// is sent as it is: the service decides about it).
    /// </summary>
    public static List<FittedPhoto> FitRequest(
        IReadOnlyList<(byte[] Content, string ContentType)> photos, long maxPhotoBytes, long maxRequestBytes, int maxSide, long maxPixels = DefaultMaxPixels)
    {
        ArgumentNullException.ThrowIfNull(photos);

        var fitted = new List<FittedPhoto>();
        for (var index = 0; index < photos.Count; index++)
        {
            var (content, contentType) = photos[index];
            if (content.LongLength <= maxPhotoBytes)
            {
                fitted.Add(new FittedPhoto(index, content, contentType, false));
                continue;
            }

            if (Scale(content, maxSide, maxPhotoBytes, maxPixels) is { } scaled)
            {
                fitted.Add(scaled with { SourceIndex = index });
            }
        }

        while (fitted.Count > 1 && fitted.Sum(photo => photo.Content.LongLength) > maxRequestBytes)
        {
            var largest = fitted.Where(photo => !photo.Scaled).OrderByDescending(photo => photo.Content.LongLength).FirstOrDefault();
            if (largest is not null)
            {
                var position = fitted.IndexOf(largest);
                var scaled = Scale(largest.Content, maxSide, maxPhotoBytes, maxPixels);

                // Scaled, or marked as tried so the loop moves on; an undecodable photo is dropped here (it is the request's largest).
                if (scaled is null)
                {
                    fitted.RemoveAt(position);
                }
                else
                {
                    fitted[position] = (scaled.Content.LongLength < largest.Content.LongLength ? scaled : largest with { Scaled = true }) with { SourceIndex = largest.SourceIndex };
                }

                continue;
            }

            fitted.Remove(fitted.OrderByDescending(photo => photo.Content.LongLength).First());
        }

        return fitted;
    }

    /// <summary>
    /// The photo decoded and scaled so its long side is at most <paramref name="maxSide"/> (never enlarged) and its encoding at most
    /// <paramref name="maxBytes"/>; null when it cannot be decoded, has more than <paramref name="maxPixels"/> pixels, or does not fit even after
    /// <see cref="ShrinkSteps"/> more steps.
    /// </summary>
    public static FittedPhoto? Scale(byte[] content, int maxSide, long maxBytes, long maxPixels = DefaultMaxPixels)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var data = SKData.CreateCopy(content);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > maxPixels)
        {
            return null;
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var source = new SKBitmap(info);
        var result = codec.GetPixels(info, source.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        var transparent = codec.Info.AlphaType != SKAlphaType.Opaque && HasTransparency(source);
        var longSide = Math.Max(info.Width, info.Height);
        var side = Math.Min(maxSide, longSide);

        for (var step = 0; step <= ShrinkSteps && side > 0; step++)
        {
            var encoded = Encode(source, side, longSide, transparent);
            if (encoded.LongLength <= maxBytes)
            {
                return new FittedPhoto(0, encoded, transparent ? "image/png" : "image/jpeg", true);
            }

            side = (int)(side * 0.8);
        }

        return null;
    }

    private static byte[] Encode(SKBitmap source, int side, int longSide, bool transparent)
    {
        var scale = (double)side / longSide;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = width == source.Width && height == source.Height
            ? source.Copy()
            : source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(resized);
        using var encoded = transparent ? image.Encode(SKEncodedImageFormat.Png, 100) : image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

        return encoded.ToArray();
    }

    /// <summary>Whether any pixel is not fully opaque (an alpha channel that is all 255 is no transparency: the photo goes as JPEG).</summary>
    private static bool HasTransparency(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != 255)
            {
                return true;
            }
        }

        return false;
    }
}
