using System.Buffers.Binary;
using System.Text;

namespace Corvid.Models.Images;

/// <summary>An image's format and size as its own bytes say, never as a file name or a sender claims.</summary>
public sealed record ImageInfo(string MediaType, int Width, int Height);

/// <summary>
/// K-8 (A6): reading and cleaning images without an imaging library. The format comes from the bytes' signature and the size
/// from their header (PNG's IHDR, JPEG's SOF, GIF's screen descriptor, WebP's VP8, VP8L or VP8X chunk). Metadata is
/// stripped by walking the format's own structure: JPEG's APP1 (EXIF, XMP, where a phone writes the location) and every
/// other application segment but JFIF and Adobe colour, and comments, in every segment through EOI (between progressive
/// scans too), and any bytes after EOI; PNG's text, EXIF and time chunks and every ancillary chunk not needed to draw it;
/// GIF's comments and application extensions but the loop count; every WebP chunk but the picture's own (VP8X, VP8, VP8L,
/// ALPH, ANIM, ANMF and, inside a frame, its ALPH, VP8 and VP8L). Anything that does not parse is refused rather than passed on.
/// <para>
/// ICC colour profiles go in every format (JPEG's APP2, PNG's iCCP, WebP's ICCP): a profile can carry the device's maker and
/// model, descriptions and dates, and none is needed to show the picture. Vision models read pixels as sRGB, so a picture
/// tagged with a wider profile (Display P3, Adobe RGB) shows slightly duller colours without it. PNG's fixed-form sRGB, gAMA
/// and cHRM chunks and JPEG's Adobe segment (needed to decode CMYK and YCCK) stay.
/// </para>
/// </summary>
public static class ImageFiles
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";

    /// <summary>The most bytes one image may have as it arrives (20 MiB).</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>The longest side an image may have, in pixels.</summary>
    public const int MaxSide = 16_384;

    /// <summary>The most pixels an image may have (40 megapixels): a small file can declare a huge canvas.</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>The most images one message may carry.</summary>
    public const int MaxPerMessage = 8;

    /// <summary>The media types Corvid accepts.</summary>
    public static IReadOnlyList<string> MediaTypes { get; } = [Png, Jpeg, Gif, Webp];

    /// <summary>Whether a file name's extension is one of an accepted image type (for <c>@path</c>; the bytes still decide).</summary>
    public static bool HasImageExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    /// <summary>The image's type and size from its bytes, or null when they are not an image Corvid accepts.</summary>
    public static ImageInfo? Inspect(ReadOnlySpan<byte> data)
    {
        try
        {
            return InspectCore(data);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks an image as it arrives and returns its bytes without metadata, with its type and size: refused (with why, for
    /// the person) when it is empty, too large, not an accepted format, malformed or of a size past <see cref="MaxSide"/> or
    /// <see cref="MaxPixels"/>.
    /// </summary>
    /// <exception cref="Abstractions.Media.ImageRefusedException">Why the image is refused.</exception>
    public static (byte[] Data, ImageInfo Info) Prepare(ReadOnlySpan<byte> data, string? name = null)
    {
        var label = name is { Length: > 0 } ? $"'{Abstractions.Media.ImageNames.Clean(name)}'" : "The pasted image";
        if (data.IsEmpty)
            throw new Abstractions.Media.ImageRefusedException($"{label} is empty.");
        if (data.Length > MaxBytes)
            throw new Abstractions.Media.ImageRefusedException($"{label} is {Size(data.Length)}; an image may be at most {Size(MaxBytes)}.");
        var info = Inspect(data)
            ?? throw new Abstractions.Media.ImageRefusedException($"{label} is not a PNG, JPEG, GIF or WebP image (read from its bytes, not its name).");
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxSide || info.Height > MaxSide || (long)info.Width * info.Height > MaxPixels)
            throw new Abstractions.Media.ImageRefusedException(
                $"{label} is {info.Width}×{info.Height} pixels; an image may be at most {MaxSide} pixels a side and {MaxPixels / 1_000_000} megapixels.");
        // A GIF's frames carry their own size and place: a small logical screen can hold a frame a decoder would draw huge.
        if (info.MediaType == Gif && GifOversizedFrame(data) is { } frame)
            throw new Abstractions.Media.ImageRefusedException(
                $"{label} has a frame of {frame.Width}×{frame.Height} pixels at {frame.Left},{frame.Top}; an image may be at most {MaxSide} pixels a side and {MaxPixels / 1_000_000} megapixels.");
        byte[] stripped;
        try
        {
            stripped = StripCore(data, info.MediaType);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            throw new Abstractions.Media.ImageRefusedException($"{label} could not be read as a {Kind(info.MediaType)} to remove its metadata, so it was not taken.");
        }
        return (stripped, info);
    }

    /// <summary>The bytes without metadata. Throws <see cref="InvalidDataException"/> when they are not an accepted image.</summary>
    public static byte[] StripMetadata(ReadOnlySpan<byte> data)
    {
        var info = Inspect(data) ?? throw new InvalidDataException("Not a PNG, JPEG, GIF or WebP image.");
        try
        {
            return StripCore(data, info.MediaType);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("The image is malformed.", exception);
        }
    }

    /// <summary>"PNG", "JPEG", "GIF" or "WebP".</summary>
    public static string Kind(string mediaType) => Abstractions.Media.ImageNames.Kind(mediaType);

    /// <summary>"84 KB", "1.2 MB", "512 B".</summary>
    public static string Size(long bytes) => Abstractions.Media.ImageNames.Size(bytes);

    // ---- inspection ----

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ImageInfo? InspectCore(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual(PngSignature))
        {
            if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
                return null;
            return new(Png, Dimension(BinaryPrimitives.ReadUInt32BigEndian(data[16..])), Dimension(BinaryPrimitives.ReadUInt32BigEndian(data[20..])));
        }
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return JpegSize(data);
        if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
            return new(Gif, BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return WebpSize(data);
        return null;
    }

    private static int Dimension(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static ImageInfo? JpegSize(ReadOnlySpan<byte> data)
    {
        var at = 2;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0xFF)
                return null;
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                at += 2;
                continue;
            }
            if (marker is 0xD9 or 0xDA)
                return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            if (length < 2 || at + 2 + length > data.Length)
                return null;
            // SOF0-3, 5-7, 9-11, 13-15: precision, then height and width.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC) && length >= 7)
                return new(Jpeg, BinaryPrimitives.ReadUInt16BigEndian(data[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]));
            at += 2 + length;
        }
        return null;
    }

    private static ImageInfo? WebpSize(ReadOnlySpan<byte> data)
    {
        var chunk = data.Slice(12, 4);
        var body = data[20..];
        if (chunk.SequenceEqual("VP8X"u8) && body.Length >= 10)
            return new(Webp, 1 + (body[4] | body[5] << 8 | body[6] << 16), 1 + (body[7] | body[8] << 8 | body[9] << 16));
        if (chunk.SequenceEqual("VP8L"u8) && body.Length >= 5 && body[0] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
            return new(Webp, 1 + (int)(bits & 0x3FFF), 1 + (int)(bits >> 14 & 0x3FFF));
        }
        if (chunk.SequenceEqual("VP8 "u8) && body.Length >= 10 && body[3] == 0x9D && body[4] == 0x01 && body[5] == 0x2A)
            return new(Webp, BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(body[8..]) & 0x3FFF);
        return null;
    }

    // ---- stripping ----

    private static byte[] StripCore(ReadOnlySpan<byte> data, string mediaType) => mediaType switch
    {
        Png => StripPng(data),
        Jpeg => StripJpeg(data),
        Gif => StripGif(data),
        Webp => StripWebp(data),
        _ => throw new InvalidDataException("Not an accepted image type."),
    };

    // Critical chunks, what drawing needs (transparency, colour, gamma, density) and APNG's animation chunks.
    private static readonly HashSet<string> PngKept = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "sBIT", "bKGD", "pHYs", "acTL", "fcTL", "fdAT",
    };

    private static byte[] StripPng(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data[..8]);
        var at = 8;
        var ended = false;
        while (at < data.Length && !ended)
        {
            if (at + 12 > data.Length)
                throw new InvalidDataException("A PNG chunk is cut short.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            if (length > int.MaxValue || at + 12 + (long)length > data.Length)
                throw new InvalidDataException("A PNG chunk runs past the end.");
            var type = Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            var whole = data.Slice(at, 12 + (int)length);
            if (PngKept.Contains(type))
                output.Write(whole);
            ended = type == "IEND";
            at += whole.Length;
        }
        if (!ended)
            throw new InvalidDataException("The PNG has no end chunk.");
        return output.ToArray();
    }

    private static byte[] StripJpeg(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data[..2]);
        var at = 2;
        while (true)
        {
            if (at + 2 > data.Length || data[at] != 0xFF)
                throw new InvalidDataException("A JPEG segment is malformed.");
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                output.Write(data.Slice(at, 2));
                at += 2;
                continue;
            }
            if (marker == 0xD9)
            {
                output.Write(data.Slice(at, 2));
                return output.ToArray();
            }
            if (at + 4 > data.Length)
                throw new InvalidDataException("A JPEG segment is cut short.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            if (length < 2 || at + 2 + length > data.Length)
                throw new InvalidDataException("A JPEG segment runs past the end.");
            var segment = data.Slice(at, 2 + length);
            at += segment.Length;
            if (marker == 0xDA)
            {
                // A scan: its header, then entropy-coded data up to the next marker (a stuffed 0xFF00 and the restart
                // markers are part of the data). The walk then goes on, so an APPn or a comment between progressive scans
                // goes too, and it ends at EOI, so nothing after the picture is kept.
                output.Write(segment);
                var scanned = EntropyEnd(data, at);
                output.Write(data[at..scanned]);
                at = scanned;
                continue;
            }
            if (KeepJpegSegment(marker, segment[4..]))
                output.Write(segment);
        }
    }

    // Where a scan's entropy-coded data ends: at the 0xFF that starts a marker other than a restart. 0xFF00 is a stuffed
    // byte; fill bytes (0xFF runs) before a marker belong to the marker, which the segment walk skips.
    private static int EntropyEnd(ReadOnlySpan<byte> data, int at)
    {
        while (at + 1 < data.Length)
        {
            if (data[at] != 0xFF)
            {
                at++;
                continue;
            }
            var next = at + 1;
            while (next < data.Length && data[next] == 0xFF)
                next++;
            if (next >= data.Length)
                break;
            if (data[next] is not (0x00 or >= 0xD0 and <= 0xD7))
                return at;
            at = next + 1;
        }
        throw new InvalidDataException("A JPEG scan has no end.");
    }

    // APP0 JFIF (not its JFXX thumbnail extension, which can be a whole second picture) and APP14 Adobe colour (decoding CMYK
    // and YCCK needs it); every other APPn (APP1's EXIF and XMP, APP2's ICC profile, APP13's IPTC) and comments go.
    private static bool KeepJpegSegment(byte marker, ReadOnlySpan<byte> body) => marker switch
    {
        0xE0 => body.StartsWith("JFIF\0"u8),
        0xEE => body.StartsWith("Adobe"u8),
        >= 0xE0 and <= 0xEF => false,
        0xFE => false,
        _ => true,
    };

    /// <summary>A GIF frame that reaches past <see cref="MaxSide"/> or <see cref="MaxPixels"/>, or null.</summary>
    private static (int Left, int Top, int Width, int Height)? GifOversizedFrame(ReadOnlySpan<byte> data)
    {
        try
        {
            var flags = data[10];
            var at = 13 + ((flags & 0x80) != 0 ? 3 * (1 << ((flags & 0x07) + 1)) : 0);
            while (at < data.Length)
            {
                switch (data[at])
                {
                    case 0x21:
                        at = SubBlocksEnd(data, at + 2);
                        break;
                    case 0x2C:
                    {
                        int left = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 1)..]);
                        int top = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 3)..]);
                        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 5)..]);
                        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 7)..]);
                        // The canvas a decoder may grow to hold the frame, as well as the frame itself.
                        if (left + width > MaxSide || top + height > MaxSide || (long)(left + width) * (top + height) > MaxPixels)
                            return (left, top, width, height);
                        var local = data[at + 9];
                        at += 10 + ((local & 0x80) != 0 ? 3 * (1 << ((local & 0x07) + 1)) : 0);
                        at = SubBlocksEnd(data, at + 1);
                        break;
                    }
                    default:
                        // The trailer, or a block stripping refuses with its own reason.
                        return null;
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            // Malformed: stripping refuses it with its own reason.
            return null;
        }
    }

    private static byte[] StripGif(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length);
        var flags = data[10];
        var at = 13 + ((flags & 0x80) != 0 ? 3 * (1 << ((flags & 0x07) + 1)) : 0);
        if (at > data.Length)
            throw new InvalidDataException("The GIF's colour table is cut short.");
        output.Write(data[..at]);
        while (true)
        {
            if (at >= data.Length)
                throw new InvalidDataException("The GIF has no trailer.");
            switch (data[at])
            {
                case 0x3B:
                    output.WriteByte(0x3B);
                    return output.ToArray();
                case 0x21:
                {
                    if (at + 2 > data.Length)
                        throw new InvalidDataException("A GIF extension is cut short.");
                    var label = data[at + 1];
                    var end = SubBlocksEnd(data, at + 2);
                    var keep = label switch
                    {
                        0xF9 or 0x01 => true,
                        // The loop count only: NETSCAPE2.0 or ANIMEXTS1.0, whose sub-block is the 11-byte identifier.
                        0xFF => at + 14 <= data.Length && data[at + 2] == 11
                            && (data.Slice(at + 3, 11).SequenceEqual("NETSCAPE2.0"u8) || data.Slice(at + 3, 11).SequenceEqual("ANIMEXTS1.0"u8)),
                        _ => false,
                    };
                    if (keep)
                        output.Write(data[at..end]);
                    at = end;
                    break;
                }
                case 0x2C:
                {
                    if (at + 10 > data.Length)
                        throw new InvalidDataException("A GIF image descriptor is cut short.");
                    var local = data[at + 9];
                    var start = at;
                    at += 10 + ((local & 0x80) != 0 ? 3 * (1 << ((local & 0x07) + 1)) : 0);
                    // The LZW minimum code size, then the data sub-blocks.
                    at = SubBlocksEnd(data, at + 1);
                    output.Write(data[start..at]);
                    break;
                }
                default:
                    throw new InvalidDataException("The GIF holds an unknown block.");
            }
        }
    }

    private static int SubBlocksEnd(ReadOnlySpan<byte> data, int at)
    {
        while (true)
        {
            if (at >= data.Length)
                throw new InvalidDataException("A GIF block runs past the end.");
            var size = data[at];
            at += 1 + size;
            if (size == 0)
                return at;
        }
    }

    // The chunks that make the picture (RFC 9649): the extended header, the bitstreams, alpha and animation. ICCP, EXIF, XMP
    // and every chunk not listed go.
    private static readonly string[] WebpKept = ["VP8X", "VP8 ", "VP8L", "ALPH", "ANIM", "ANMF"];

    // Inside an ANMF frame, after its 16-byte header: the frame's alpha and bitstream; anything else goes.
    private static readonly string[] WebpFrameKept = ["ALPH", "VP8 ", "VP8L"];

    private static byte[] StripWebp(ReadOnlySpan<byte> data)
    {
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var end = (int)Math.Min(data.Length, 8L + declared);
        using var output = new MemoryStream(data.Length);
        output.Write(data[..12]);
        WriteWebpChunks(data[12..end], output, WebpKept, inFrame: false);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static void WriteWebpChunks(ReadOnlySpan<byte> chunks, MemoryStream output, string[] kept, bool inFrame)
    {
        var at = 0;
        while (at + 8 <= chunks.Length)
        {
            var type = Encoding.ASCII.GetString(chunks.Slice(at, 4));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunks[(at + 4)..]);
            var padded = length + (length & 1);
            if (at + 8 + (long)length > chunks.Length)
                throw new InvalidDataException("A WebP chunk runs past the end.");
            var whole = chunks.Slice(at, (int)Math.Min(8 + padded, chunks.Length - at));
            at += whole.Length;
            if (!kept.Contains(type, StringComparer.Ordinal))
                continue;
            if (type == "VP8X" && length >= 10)
            {
                var copy = whole.ToArray();
                // The ICC (bit 5), EXIF (bit 3) and XMP (bit 2) flags go with their chunks.
                copy[8] = (byte)(copy[8] & ~0x2C);
                output.Write(copy);
            }
            else if (type == "ANMF" && !inFrame)
            {
                if (length < 16)
                    throw new InvalidDataException("A WebP frame is cut short.");
                using var frame = new MemoryStream();
                frame.Write(whole.Slice(8, 16));
                WriteWebpChunks(whole.Slice(24, (int)length - 16), frame, WebpFrameKept, inFrame: true);
                var body = frame.ToArray();
                var header = new byte[8];
                "ANMF"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)body.Length);
                output.Write(header);
                output.Write(body);
                if ((body.Length & 1) != 0)
                    output.WriteByte(0);
            }
            else
            {
                output.Write(whole);
            }
        }
    }
}
