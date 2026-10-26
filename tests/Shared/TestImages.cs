using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Corvid.Tests;

/// <summary>
/// K-8 (A6): small fixture images built in code, each carrying the metadata a phone or an editor writes, so a test can say
/// it is gone: a PNG with a text chunk and an eXIf chunk naming a place, a JPEG with an EXIF APP1 segment (GPS) and a
/// comment, a GIF with a comment extension, a WebP with EXIF and XMP chunks. The PNG is a real, decodable image.
/// </summary>
internal static class TestImages
{
    /// <summary>The marker every fixture's metadata holds; a stripped image must not contain it.</summary>
    public const string Secret = "GPS 51.5007N 0.1246W home";

    /// <summary>A decodable PNG of the given size (a solid colour), with tEXt and eXIf chunks holding <see cref="Secret"/>.</summary>
    public static byte[] Png(int width = 4, int height = 3, bool metadata = true)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolour
        Chunk(output, "IHDR", header);
        if (metadata)
        {
            Chunk(output, "tEXt", Encoding.Latin1.GetBytes("Comment\0" + Secret));
            Chunk(output, "eXIf", Exif());
            Chunk(output, "tIME", [0x07, 0xEA, 10, 25, 12, 0, 0]);
        }
        var raw = new byte[height * (1 + width * 3)];
        for (var row = 0; row < height; row++)
            for (var x = 0; x < width; x++)
            {
                var at = row * (1 + width * 3) + 1 + x * 3;
                raw[at] = 0x20;
                raw[at + 1] = 0x80;
                raw[at + 2] = 0xC0;
            }
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            Chunk(output, "IDAT", compressed.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>A structurally valid JPEG (SOI, JFIF, EXIF with <see cref="Secret"/>, a comment, tables, SOF0, a scan, EOI).</summary>
    public static byte[] Jpeg(int width = 640, int height = 480)
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        Segment(output, 0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(output, 0xE1, [.. "Exif\0\0"u8, .. Exif()]);
        Segment(output, 0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Encoding.ASCII.GetBytes("<x:xmpmeta>" + Secret + "</x:xmpmeta>")]);
        Segment(output, 0xFE, Encoding.ASCII.GetBytes(Secret));
        Segment(output, 0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);
        var frame = new byte[] { 8, 0, 0, 0, 0, 1, 1, 0x11, 0 };
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)width);
        Segment(output, 0xC0, frame);
        Segment(output, 0xDA, [1, 1, 0, 0, 0x3F, 0]);
        output.Write([0x12, 0x34, 0xFF, 0x00, 0x56]);
        output.Write([0xFF, 0xD9]);
        return output.ToArray();
    }

    /// <summary>A GIF with a comment extension and an application extension holding <see cref="Secret"/>, and a loop count.</summary>
    public static byte[] Gif(int width = 2, int height = 2)
    {
        using var output = new MemoryStream();
        output.Write("GIF89a"u8);
        Span<byte> screen = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(screen, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(screen[2..], (ushort)height);
        screen[4] = 0x80; // a global colour table of 2 entries
        output.Write(screen);
        output.Write([0, 0, 0, 255, 255, 255]);
        output.Write([0x21, 0xFF, 11, .. "NETSCAPE2.0"u8, 3, 1, 0, 0, 0]);
        var comment = Encoding.ASCII.GetBytes(Secret);
        output.Write([0x21, 0xFE, (byte)comment.Length, .. comment, 0]);
        output.Write([0x21, 0xFF, 11, .. "XMP DataXMP"u8, (byte)comment.Length, .. comment, 0]);
        output.Write([0x2C, 0, 0, 0, 0, (byte)width, 0, (byte)height, 0, 0]);
        output.Write([2, 2, 0x44, 0x01, 0]);
        output.WriteByte(0x3B);
        return output.ToArray();
    }

    /// <summary>A WebP (VP8X with the EXIF and XMP flags, an EXIF and an XMP chunk holding <see cref="Secret"/>, a VP8L).</summary>
    public static byte[] Webp(int width = 16, int height = 9)
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        var vp8x = new byte[10];
        vp8x[0] = 0x0C;
        vp8x[4] = (byte)(width - 1);
        vp8x[5] = (byte)((width - 1) >> 8);
        vp8x[7] = (byte)(height - 1);
        vp8x[8] = (byte)((height - 1) >> 8);
        RiffChunk(body, "VP8X", vp8x);
        RiffChunk(body, "EXIF", Exif());
        RiffChunk(body, "XMP ", Encoding.ASCII.GetBytes(Secret));
        var bits = (uint)(width - 1) | (uint)(height - 1) << 14;
        var lossless = new byte[] { 0x2F, 0, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt32LittleEndian(lossless.AsSpan(1), bits);
        RiffChunk(body, "VP8L", lossless);
        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        output.Write(size);
        output.Write(body.ToArray());
        return output.ToArray();
    }

    /// <summary>
    /// A6 review: a progressive JPEG with <see cref="Secret"/> where a walk that stops at the first scan keeps it: an ICC
    /// profile before the frame, an APP1 and a comment between two scans, and bytes after EOI. Its scans hold a stuffed
    /// 0xFF00, a restart marker and fill bytes, which are image data.
    /// </summary>
    public static byte[] ProgressiveJpeg(int width = 64, int height = 48)
    {
        var secret = Encoding.ASCII.GetBytes(Secret);
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        Segment(output, 0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(output, 0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. secret]);
        Segment(output, 0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);
        var frame = new byte[] { 8, 0, 0, 0, 0, 1, 1, 0x11, 0 };
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)width);
        Segment(output, 0xC2, frame);
        Segment(output, 0xC4, [0, 1, .. new byte[15], 0]);
        Segment(output, 0xDA, [1, 1, 0, 0, 0, 0]);
        output.Write([0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0xFF, 0xFF]);
        Segment(output, 0xE1, [.. "Exif\0\0"u8, .. Exif()]);
        Segment(output, 0xFE, secret);
        Segment(output, 0xC4, [0x10, 1, .. new byte[15], 0]);
        Segment(output, 0xDA, [1, 1, 0, 1, 63, 0]);
        output.Write([0x9A, 0xBC, 0xFF, 0x00]);
        output.Write([0xFF, 0xD9]);
        output.Write(secret);
        return output.ToArray();
    }

    /// <summary>A6 review: a PNG with an iCCP chunk whose profile name and data hold <see cref="Secret"/>.</summary>
    public static byte[] PngWithIcc()
    {
        var png = Png(metadata: false);
        using var output = new MemoryStream();
        // After the signature and IHDR (8 + 25 bytes), as an encoder writes it.
        output.Write(png.AsSpan(0, 33));
        Chunk(output, "iCCP", [.. "ICC\0"u8, 0, .. Encoding.ASCII.GetBytes(Secret)]);
        output.Write(png.AsSpan(33));
        return output.ToArray();
    }

    /// <summary>
    /// A6 review: an animated WebP with an ICCP chunk (and its flag), a chunk no decoder knows, and a frame holding a chunk of
    /// its own beside its bitstream, each with <see cref="Secret"/>.
    /// </summary>
    public static byte[] AnimatedWebp(int width = 16, int height = 9)
    {
        var secret = Encoding.ASCII.GetBytes(Secret);
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        var vp8x = new byte[10];
        vp8x[0] = 0x20 | 0x02; // ICC and animation
        vp8x[4] = (byte)(width - 1);
        vp8x[7] = (byte)(height - 1);
        RiffChunk(body, "VP8X", vp8x);
        RiffChunk(body, "ICCP", secret);
        RiffChunk(body, "ANIM", [0, 0, 0, 0, 0, 0]);
        using (var frame = new MemoryStream())
        {
            var header = new byte[16];
            header[6] = (byte)(width - 1);
            header[9] = (byte)(height - 1);
            frame.Write(header);
            RiffChunk(frame, "ZZZZ", secret);
            var bits = (uint)(width - 1) | (uint)(height - 1) << 14;
            var lossless = new byte[] { 0x2F, 0, 0, 0, 0, 0 };
            BinaryPrimitives.WriteUInt32LittleEndian(lossless.AsSpan(1), bits);
            RiffChunk(frame, "VP8L", lossless);
            RiffChunk(body, "ANMF", frame.ToArray());
        }
        RiffChunk(body, "UNKN", secret);
        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        output.Write(size);
        output.Write(body.ToArray());
        return output.ToArray();
    }

    /// <summary>A6 review: a GIF whose logical screen is small and whose one frame is placed and sized as given.</summary>
    public static byte[] GifWithFrame(int left, int top, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write("GIF89a"u8);
        output.Write([2, 0, 2, 0, 0x80, 0, 0]);
        output.Write([0, 0, 0, 255, 255, 255]);
        Span<byte> descriptor = stackalloc byte[10];
        descriptor[0] = 0x2C;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[1..], (ushort)left);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[3..], (ushort)top);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..], (ushort)height);
        output.Write(descriptor);
        output.Write([2, 2, 0x44, 0x01, 0]);
        output.WriteByte(0x3B);
        return output.ToArray();
    }

    /// <summary>Whether the bytes still hold <see cref="Secret"/> anywhere.</summary>
    public static bool HoldsSecret(ReadOnlySpan<byte> data) => data.IndexOf(Encoding.ASCII.GetBytes(Secret)) >= 0;

    // A little-endian TIFF header with one IFD entry pointing at an ASCII value holding the secret.
    private static byte[] Exif()
    {
        var text = Encoding.ASCII.GetBytes(Secret + "\0");
        using var output = new MemoryStream();
        output.Write("II*\0"u8);
        output.Write([8, 0, 0, 0, 1, 0, 0x0E, 0x01, 2, 0]);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)text.Length);
        output.Write(count);
        output.Write([26, 0, 0, 0, 0, 0, 0, 0]);
        output.Write(text);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        output.Write(crc);
    }

    private static void Segment(Stream output, byte marker, byte[] data)
    {
        output.Write([0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]);
        output.Write(data);
    }

    private static void RiffChunk(Stream output, string type, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(type));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(data);
        if (data.Length % 2 == 1)
            output.WriteByte(0);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
