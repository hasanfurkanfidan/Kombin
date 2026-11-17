using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kombin.Api.Common.Storage;

/// <summary>An image whose bytes are its own: the same picture as the source, a different file.</summary>
internal sealed record MarkedImage(byte[] Content, string ContentType);

/// <summary>
/// Makes a copy of an image differ in bytes from its source and from every other copy, without decoding it (there is no image library here):
/// a block of random bytes (<see cref="MinNonce"/> to <see cref="MaxNonce"/> long, so the size varies too) goes into a part of the file every
/// decoder must skip. The pixels and their compressed stream are untouched, so the picture looks the same; what changes is everything a machine
/// compares without looking: the SHA-256, the store's ETag (the MD5 of a single PUT on S3, MinIO and R2) and the exact length.
/// <list type="bullet">
/// <item>JPEG: a <c>COM</c> segment right after <c>SOI</c> (the format's comment segment, ignored by decoders).</item>
/// <item>PNG: a private ancillary chunk <c>koMb</c> before <c>IEND</c> (lowercase first letter: "ancillary, ignore if unknown", with its CRC).</item>
/// <item>WEBP: an unknown chunk <c>KMBN</c> at the end of an extended (<c>VP8X</c>) file, where the container specification lets readers skip
/// unknown chunks; a simple file (<c>VP8 </c> or <c>VP8L</c> alone) is first rewritten as an extended one, its <c>VP8X</c> header taking the canvas
/// size (and the alpha flag of a lossless image) from the bitstream header.</item>
/// </list>
/// The format is read from the bytes. Anything else (or a file that does not parse) returns null: the caller must not store an unmarked copy.
/// </summary>
internal static class ImageMarker
{
    public const int MinNonce = 16;

    public const int MaxNonce = MinNonce + 255;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>The image with a fresh random block, or null when it is not a JPEG, PNG or WEBP that parses.</summary>
    public static MarkedImage? Mark(byte[] image)
    {
        var nonce = new byte[MinNonce + RandomNumberGenerator.GetInt32(0, MaxNonce - MinNonce + 1)];
        RandomNumberGenerator.Fill(nonce);

        return Mark(image, nonce);
    }

    /// <summary>The image with <paramref name="nonce"/> as its block (tests pass their own).</summary>
    public static MarkedImage? Mark(byte[] image, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (nonce.Length is < 1 or > 65_533)
        {
            throw new ArgumentOutOfRangeException(nameof(nonce), "The block must hold 1 to 65,533 bytes.");
        }

        if (image.Length >= 4 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF)
        {
            return new MarkedImage(MarkJpeg(image, nonce), "image/jpeg");
        }

        if (image.AsSpan().StartsWith(PngSignature))
        {
            return MarkPng(image, nonce) is { } png ? new MarkedImage(png, "image/png") : null;
        }

        if (image.Length >= 12 && image.AsSpan(0, 4).SequenceEqual("RIFF"u8) && image.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return MarkWebp(image, nonce) is { } webp ? new MarkedImage(webp, "image/webp") : null;
        }

        return null;
    }

    private static byte[] MarkJpeg(byte[] image, ReadOnlySpan<byte> nonce)
    {
        var segment = new byte[4 + nonce.Length];
        segment[0] = 0xFF;
        segment[1] = 0xFE;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(2 + nonce.Length));
        nonce.CopyTo(segment.AsSpan(4));

        return [.. image.AsSpan(0, 2), .. segment, .. image.AsSpan(2)];
    }

    private static byte[]? MarkPng(byte[] image, ReadOnlySpan<byte> nonce)
    {
        var position = PngSignature.Length;

        while (position + 12 <= image.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(position));
            if (length > int.MaxValue - 12 || position + 12 + (long)length > image.Length)
            {
                return null;
            }

            if (image.AsSpan(position + 4, 4).SequenceEqual("IEND"u8))
            {
                var chunk = new byte[12 + nonce.Length];
                BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)nonce.Length);
                "koMb"u8.CopyTo(chunk.AsSpan(4));
                nonce.CopyTo(chunk.AsSpan(8));
                BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + nonce.Length), Crc32(chunk.AsSpan(4, 4 + nonce.Length)));

                return [.. image.AsSpan(0, position), .. chunk, .. image.AsSpan(position)];
            }

            position += 12 + (int)length;
        }

        return null;
    }

    private static byte[]? MarkWebp(byte[] image, ReadOnlySpan<byte> nonce)
    {
        var riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4));
        if (riffEnd > image.Length)
        {
            return null;
        }

        // Every chunk must parse, so nothing is appended to a file that is not what it claims to be.
        var chunks = new List<(int Start, int Total)>();
        for (var position = 12; position < riffEnd;)
        {
            if (position + 8 > riffEnd)
            {
                return null;
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(position + 4));
            var total = 8L + size + (size & 1);
            if (position + 8 + (long)size > riffEnd)
            {
                return null;
            }

            total = Math.Min(total, riffEnd - position);
            chunks.Add((position, (int)total));
            position += (int)total;
        }

        if (chunks.Count == 0)
        {
            return null;
        }

        var first = image.AsSpan(chunks[0].Start, 4);
        byte[] header;
        if (first.SequenceEqual("VP8X"u8))
        {
            header = [];
        }
        else if (CanvasOf(image, chunks[0].Start) is { } canvas)
        {
            header = new byte[18];
            "VP8X"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 10);
            header[8] = canvas.Alpha ? (byte)0x10 : (byte)0;
            WriteUInt24(header.AsSpan(12), canvas.Width - 1);
            WriteUInt24(header.AsSpan(15), canvas.Height - 1);
        }
        else
        {
            return null;
        }

        var padded = nonce.Length + (nonce.Length & 1);
        var unknown = new byte[8 + padded];
        "KMBN"u8.CopyTo(unknown);
        BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(4), (uint)nonce.Length);
        nonce.CopyTo(unknown.AsSpan(8));

        // A last chunk of odd size without its padding byte gets one, so the new chunk starts on an even offset.
        byte[] pad = ((riffEnd - 12) & 1) == 1 ? [0] : [];
        byte[] content = [.. image.AsSpan(0, 12), .. header, .. image.AsSpan(12, (int)riffEnd - 12), .. pad, .. unknown];
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(4), (uint)(content.Length - 8));

        return content;
    }

    /// <summary>The canvas of a simple WEBP from its bitstream header: <c>VP8 </c> (lossy key frame) or <c>VP8L</c> (lossless); null otherwise.</summary>
    private static (int Width, int Height, bool Alpha)? CanvasOf(byte[] image, int chunkStart)
    {
        var type = image.AsSpan(chunkStart, 4);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(chunkStart + 4));
        var data = image.AsSpan(chunkStart + 8, (int)Math.Min(size, (uint)(image.Length - chunkStart - 8)));

        // Lossy: 3-byte frame tag (bit 0 clear on a key frame), start code 9D 01 2A, then 14-bit width and height.
        if (type.SequenceEqual("VP8 "u8) && data.Length >= 10 && (data[0] & 1) == 0 && data[3] == 0x9D && data[4] == 0x01 && data[5] == 0x2A)
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF, false);
        }

        // Lossless: signature 0x2F, then 14 bits width − 1, 14 bits height − 1, 1 bit alpha_is_used.
        if (type.SequenceEqual("VP8L"u8) && data.Length >= 5 && data[0] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, ((bits >> 28) & 1) == 1);
        }

        return null;
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    /// <summary>CRC-32 (ISO 3309, the PNG chunk checksum) over the chunk type and data.</summary>
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var index = 0u; index < 256; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
