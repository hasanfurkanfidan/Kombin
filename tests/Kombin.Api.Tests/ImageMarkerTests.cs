using System.Buffers.Binary;
using System.Security.Cryptography;
using Kombin.Api.Common.Storage;

namespace Kombin.Api.Tests;

/// <summary>
/// <see cref="ImageMarker"/>: every copy of an image differs in bytes (SHA-256, length) from the source and from every other copy (so does the
/// store's ETag, the MD5 of the body of a single PUT on S3, MinIO and R2: it is not computed here, MD5 being banned by CA5351), while the image data is carried over unchanged inside a structure decoders accept. The samples are 4 × 3 images encoded by
/// Pillow; their marked versions were decoded by Pillow when the marker was written (task L3-fix).
/// </summary>
public class ImageMarkerTests
{
    public static readonly byte[] WebpLossy = Convert.FromBase64String("UklGRjoAAABXRUJQVlA4IC4AAACQAQCdASoEAAMAAUAmJaACdLoAA5gA/vi1F/94z/+4z/+4z/37f1qoVO/pAAAA");

    public static readonly byte[] WebpLossless = Convert.FromBase64String("UklGRh4AAABXRUJQVlA4TBEAAAAvA4AAAAdQlFIUuf+BiOh/AAA=");

    public static readonly byte[] WebpAlpha = Convert.FromBase64String(
        "UklGRl4AAABXRUJQVlA4WAoAAAAQAAAAAwAAAgAAQUxQSAoAAAABB1DAiAhERP8DVlA4IC4AAACQAQCdASoEAAMAAUAmJaACdLoAA5gA/vi1F/94z/+4z/+4z/37f1qoVO/pAAAA");

    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7ljmRAAAAE0lEQVR4nGMU0TjBAANMcBY6BwAlfAEKIUdVuAAAAABJRU5ErkJggg==");

    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF\0"u8, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0xFF, 0xD9];

    public static TheoryData<string> Formats => ["webp-lossy", "webp-lossless", "webp-alpha", "png", "jpeg"];

    private static byte[] Sample(string format) => format switch
    {
        "webp-lossy" => WebpLossy,
        "webp-lossless" => WebpLossless,
        "webp-alpha" => WebpAlpha,
        "png" => Png,
        _ => Jpeg,
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void EveryCopyHasItsOwnBytesHashAndUsuallyLength(string format)
    {
        var source = Sample(format);
        var copies = Enumerable.Range(0, 20).Select(_ => ImageMarker.Mark(source)!.Content).ToList();

        var hashes = copies.Select(copy => Convert.ToHexString(SHA256.HashData(copy))).Append(Convert.ToHexString(SHA256.HashData(source))).ToList();

        Assert.Equal(21, hashes.Distinct().Count());
        Assert.True(copies.Select(copy => copy.Length).Distinct().Count() > 1, "the block's length varies, so the size does too");
        Assert.All(copies, copy => Assert.InRange(copy.Length - source.Length, ImageMarker.MinNonce, ImageMarker.MaxNonce + 32));
    }

    [Fact]
    public void AJpegGetsACommentSegmentAfterTheStartOfImage()
    {
        var marked = ImageMarker.Mark(Jpeg, [1, 2, 3])!;

        Assert.Equal("image/jpeg", marked.ContentType);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x05, 1, 2, 3], marked.Content[..9]);
        Assert.Equal(Jpeg[2..], marked.Content[9..]);
    }

    [Fact]
    public void APngGetsAnAncillaryPrivateChunkWithAValidCrcBeforeIend()
    {
        var marked = ImageMarker.Mark(Png, [1, 2, 3])!;
        var chunks = PngChunks(marked.Content);

        Assert.Equal("image/png", marked.ContentType);
        Assert.Equal(["IHDR", "IDAT", "koMb", "IEND"], chunks.Select(chunk => chunk.Type));
        Assert.Equal([1, 2, 3], chunks[2].Data);
        Assert.True(char.IsLower(chunks[2].Type[0]) && char.IsLower(chunks[2].Type[1]) && char.IsUpper(chunks[2].Type[2]), "ancillary, private, reserved bit clear");

        // Every original chunk is carried over byte for byte.
        Assert.Equal(PngChunks(Png).Select(chunk => (chunk.Type, Convert.ToHexString(chunk.Data))), chunks.Where(chunk => chunk.Type != "koMb").Select(chunk => (chunk.Type, Convert.ToHexString(chunk.Data))));
    }

    [Theory]
    [InlineData("webp-lossy", false)]
    [InlineData("webp-lossless", false)]
    public void ASimpleWebpBecomesAnExtendedOneWithTheCanvasOfItsBitstreamAndAnUnknownChunkAtTheEnd(string format, bool alpha)
    {
        var source = Sample(format);
        var marked = ImageMarker.Mark(source, [1, 2, 3])!;
        var chunks = WebpChunks(marked.Content);

        Assert.Equal("image/webp", marked.ContentType);
        Assert.Equal(marked.Content.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(marked.Content.AsSpan(4)));
        Assert.Equal("VP8X", chunks[0].Type);
        Assert.Equal(alpha ? 0x10 : 0, chunks[0].Data[0]);
        Assert.Equal((4, 3), (U24(chunks[0].Data, 4) + 1, U24(chunks[0].Data, 7) + 1));
        Assert.Equal(("KMBN", "010203"), (chunks[^1].Type, Convert.ToHexString(chunks[^1].Data)));
        Assert.Equal(WebpChunks(source).Select(chunk => (chunk.Type, Convert.ToHexString(chunk.Data))), chunks[1..^1].Select(chunk => (chunk.Type, Convert.ToHexString(chunk.Data))));
    }

    [Fact]
    public void AnExtendedWebpKeepsItsHeaderAndGetsTheUnknownChunkAtTheEnd()
    {
        var marked = ImageMarker.Mark(WebpAlpha, [9, 8, 7, 6])!;
        var chunks = WebpChunks(marked.Content);

        Assert.Equal(["VP8X", "ALPH", "VP8 ", "KMBN"], chunks.Select(chunk => chunk.Type));
        Assert.Equal(WebpChunks(WebpAlpha).Select(chunk => Convert.ToHexString(chunk.Data)), chunks[..^1].Select(chunk => Convert.ToHexString(chunk.Data)));
        Assert.Equal(marked.Content.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(marked.Content.AsSpan(4)));
    }

    [Theory]
    [InlineData("thumb-webp-bytes")]
    [InlineData("GIF89a....")]
    [InlineData("RIFF\u0010\0\0\0WEBPVP8 ")]
    public void AnythingElseIsNotMarked(string content) => Assert.Null(ImageMarker.Mark(System.Text.Encoding.Latin1.GetBytes(content)));

    [Fact]
    public void ATruncatedPngIsNotMarked() => Assert.Null(ImageMarker.Mark(Png[..^12]));

    private static int U24(byte[] bytes, int at) => bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16);

    private static List<(string Type, byte[] Data)> PngChunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        for (var position = 8; position < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            var type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png[(position + 8)..(position + 8 + length)];
            Assert.Equal(PngCrc(png.AsSpan(position + 4, 4 + length)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + length)));
            chunks.Add((type, data));
            position += 12 + length;
        }

        return chunks;
    }

    private static List<(string Type, byte[] Data)> WebpChunks(byte[] webp)
    {
        var chunks = new List<(string, byte[])>();
        for (var position = 12; position < webp.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(position + 4));
            chunks.Add((System.Text.Encoding.ASCII.GetString(webp, position, 4), webp[(position + 8)..(position + 8 + size)]));
            position += 8 + size + (size & 1);
        }

        return chunks;
    }

    private static uint PngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
