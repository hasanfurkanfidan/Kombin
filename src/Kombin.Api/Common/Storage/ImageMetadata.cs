using System.Buffers.Binary;

namespace Kombin.Api.Common.Storage;

/// <summary>An image without its metadata. <c>Orientation</c> is the EXIF orientation that was kept (2-8), null when the photo is upright.</summary>
internal sealed record StrippedImage(byte[] Content, string ContentType, int? Orientation, int RemovedSegments);

/// <summary>
/// Removes metadata from an uploaded photo without decoding it (there is no image library here): EXIF (GPS position, camera serial,
/// time), XMP, IPTC, comments and embedded thumbnails. The pixels are untouched.
/// <list type="bullet">
/// <item>JPEG: drops every APP1 (EXIF, XMP), APP3-APP13 and APP15 (IPTC, Photoshop, maker data), APP2 other than an ICC profile (MPF holds
/// extra pictures), APP0 other than JFIF (JFXX holds a thumbnail) and COM. A non-upright EXIF orientation is written back as a minimal
/// one-entry EXIF, otherwise the photo would show sideways. Everything from the first SOS on is copied as it is.</item>
/// <item>PNG: drops <c>eXIf</c>, <c>tEXt</c>, <c>zTXt</c>, <c>iTXt</c> and <c>tIME</c>.</item>
/// <item>WEBP: drops <c>EXIF</c> and <c>XMP </c> and clears their VP8X flags.</item>
/// </list>
/// The format is read from the bytes, not from the declared content type. Anything that cannot be parsed returns null: the caller refuses
/// it rather than storing metadata it could not remove.
/// </summary>
internal static class ImageMetadata
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly HashSet<string> PngDropped = new(["eXIf", "tEXt", "zTXt", "iTXt", "tIME"], StringComparer.Ordinal);

    public static StrippedImage? Strip(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Length >= 4 && image[0] == 0xFF && image[1] == 0xD8)
        {
            return StripJpeg(image);
        }

        if (image.AsSpan().StartsWith(PngSignature))
        {
            return StripPng(image);
        }

        if (image.Length >= 12 && Ascii(image, 0, 4) == "RIFF" && Ascii(image, 8, 4) == "WEBP")
        {
            return StripWebp(image);
        }

        return null;
    }

    /// <summary>The APP1 segment of a minimal big-endian EXIF holding only the orientation tag (0x0112).</summary>
    public static byte[] OrientationSegment(int orientation)
    {
        byte[] segment =
        [
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];

        return segment;
    }

    private static StrippedImage? StripJpeg(byte[] image)
    {
        using var output = new MemoryStream(image.Length);
        output.Write(image, 0, 2);
        var removed = 0;
        int? orientation = null;
        var orientationPosition = 2L;
        var position = 2;

        while (position < image.Length)
        {
            if (image[position] != 0xFF || position + 1 >= image.Length)
            {
                return null;
            }

            var marker = image[position + 1];

            // Fill bytes between segments.
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            // Start of scan: the compressed data (and any later segments of a progressive JPEG) are copied as they are.
            if (marker == 0xDA)
            {
                output.Write(image, position, image.Length - position);
                break;
            }

            if (marker == 0xD9)
            {
                output.Write(image, position, 2);
                break;
            }

            // Markers without a length.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                output.Write(image, position, 2);
                position += 2;
                continue;
            }

            if (position + 4 > image.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(position + 2));
            if (length < 2 || position + 2 + length > image.Length)
            {
                return null;
            }

            var data = image.AsSpan(position + 4, length - 2);
            var keep = marker switch
            {
                0xE0 => data.StartsWith("JFIF\0"u8),
                0xE1 => false,
                0xE2 => data.StartsWith("ICC_PROFILE\0"u8),
                0xEE => true,
                >= 0xE3 and <= 0xEF => false,
                0xFE => false,
                _ => true,
            };

            if (marker == 0xE1 && data.StartsWith("Exif\0\0"u8))
            {
                orientation ??= ReadOrientation(data[6..]);
            }

            if (keep)
            {
                output.Write(image, position, 2 + length);
                if (marker == 0xE0)
                {
                    orientationPosition = output.Position;
                }
            }
            else
            {
                removed++;
            }

            position += 2 + length;
        }

        var content = output.ToArray();
        if (orientation is >= 2 and <= 8)
        {
            content = [.. content.AsSpan(0, (int)orientationPosition), .. OrientationSegment(orientation.Value), .. content.AsSpan((int)orientationPosition)];
        }
        else
        {
            orientation = null;
        }

        return new StrippedImage(content, "image/jpeg", orientation, removed);
    }

    /// <summary>The orientation tag of IFD0 in a TIFF structure (the EXIF payload), or null.</summary>
    private static int? ReadOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            little = true;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            little = false;
        }
        else
        {
            return null;
        }

        ushort U16(ReadOnlySpan<byte> span, int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(span[at..]) : BinaryPrimitives.ReadUInt16BigEndian(span[at..]);
        uint U32(ReadOnlySpan<byte> span, int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(span[at..]) : BinaryPrimitives.ReadUInt32BigEndian(span[at..]);

        var ifd = U32(tiff, 4);
        if (ifd > (uint)(tiff.Length - 2))
        {
            return null;
        }

        var count = U16(tiff, (int)ifd);
        for (var index = 0; index < count; index++)
        {
            var entry = (int)ifd + 2 + (index * 12);
            if (entry + 12 > tiff.Length)
            {
                return null;
            }

            if (U16(tiff, entry) == 0x0112 && U16(tiff, entry + 2) == 3)
            {
                return U16(tiff, entry + 8);
            }
        }

        return null;
    }

    private static StrippedImage? StripPng(byte[] image)
    {
        using var output = new MemoryStream(image.Length);
        output.Write(image, 0, PngSignature.Length);
        var removed = 0;
        var position = PngSignature.Length;

        while (true)
        {
            if (position + 12 > image.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(position));
            if (length > int.MaxValue - 12 || position + 12 + (long)length > image.Length)
            {
                return null;
            }

            var type = Ascii(image, position + 4, 4);
            var total = 12 + (int)length;

            if (PngDropped.Contains(type))
            {
                removed++;
            }
            else
            {
                output.Write(image, position, total);
            }

            position += total;
            if (type == "IEND")
            {
                return new StrippedImage(output.ToArray(), "image/png", null, removed);
            }
        }
    }

    private static StrippedImage? StripWebp(byte[] image)
    {
        var chunks = new List<(string Type, int Start, int Total)>();
        var position = 12;

        while (position < image.Length)
        {
            if (position + 8 > image.Length)
            {
                return null;
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(position + 4));
            var total = 8L + size + (size & 1);
            if (position + 8 + (long)size > image.Length)
            {
                return null;
            }

            chunks.Add((Ascii(image, position, 4), position, (int)Math.Min(total, image.Length - position)));
            position += (int)Math.Min(total, image.Length - position);
        }

        using var output = new MemoryStream(image.Length);
        output.Write(image, 0, 12);
        var removed = 0;

        foreach (var (type, start, total) in chunks)
        {
            if (type is "EXIF" or "XMP ")
            {
                removed++;
                continue;
            }

            var bytes = image.AsSpan(start, total).ToArray();
            if (type == "VP8X" && bytes.Length > 8)
            {
                // Flags: 0x08 EXIF, 0x04 XMP.
                bytes[8] &= unchecked((byte)~0x0C);
            }

            output.Write(bytes);
        }

        var content = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(4), (uint)(content.Length - 8));

        return new StrippedImage(content, "image/webp", null, removed);
    }

    private static string Ascii(byte[] bytes, int start, int length) => System.Text.Encoding.ASCII.GetString(bytes, start, length);
}
