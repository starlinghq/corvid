using System.Globalization;
using System.Text;

namespace Corvid.Abstractions.Media;

/// <summary>
/// K-8: an image a message carries, as the event log records it. The bytes are never in the event: they are in the media
/// store, under their SHA-256 (content-addressed, so a fork's copied events still find them and the same picture sent twice
/// is stored once). The bytes stored are the ones a model is sent: checked and stripped of metadata (EXIF location, text
/// chunks) when they arrived.
/// </summary>
/// <param name="Digest">The SHA-256 of the stored bytes, 64 lowercase hex digits.</param>
/// <param name="MediaType"><c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c> or <c>image/webp</c>, read from the bytes.</param>
/// <param name="Bytes">The stored size.</param>
/// <param name="Name">The file name it came with (no directory), or null for a paste. Outside text: shown cleaned.</param>
public sealed record ImageRef(string Digest, string MediaType, long Bytes, int Width, int Height, string? Name = null);

/// <summary>
/// K-8: an image as a surface hands it in, before the kernel checks and stores it: the bytes as read (a paste, a dropped or
/// named file, an ACP image block) and the file name when there is one. Untrusted: nothing about it is believed until the
/// kernel has read its format from the bytes.
/// </summary>
public sealed record ImageInput(byte[] Data, string? Name = null)
{
    public byte[] Data { get; } = Data ?? throw new ArgumentNullException(nameof(Data));
}

/// <summary>K-8: where images are kept, by digest. Content-addressed and append-only: nothing is ever rewritten or deleted.</summary>
public interface IMediaStore
{
    /// <summary>Stores the bytes (idempotent: the same bytes are kept once) and returns their digest.</summary>
    string Put(ReadOnlySpan<byte> data);

    /// <summary>The bytes stored under <paramref name="digest"/>, checked against it, or null when none are (or they were changed).</summary>
    byte[]? Read(string digest);
}

/// <summary>K-8: an image a surface handed in was refused (not an image, too large, too many); the message is for the person.</summary>
public sealed class ImageRefusedException(string message) : Exception(message)
{
    public ImageRefusedException()
        : this("The image was refused.")
    {
    }

    public ImageRefusedException(string message, Exception innerException)
        : this(message + " " + innerException.Message)
    {
    }
}

/// <summary>K-8: how an image is named and sized in labels (the prompt's, the transcript's, a refusal's).</summary>
public static class ImageNames
{
    /// <summary>The longest file name kept, in characters.</summary>
    public const int MaxName = 120;

    /// <summary>
    /// A file name as an image keeps it: the last path segment, controls, format characters and line breaks removed (a
    /// name is outside text: a bidirectional override could disguise it, a line break could end a prompt's label), brackets
    /// made parentheses, cut to <see cref="MaxName"/>; null when nothing is left.
    /// </summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var last = name.Replace('\\', '/');
        last = last[(last.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(last.Length);
        foreach (var rune in last.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            if (builder.Length + rune.Utf16SequenceLength > MaxName)
                break;
            builder.Append(rune.ToString());
        }
        var cleaned = builder.ToString().Replace('[', '(').Replace(']', ')').Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>"84 KB", "1.2 MB", "512 B".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    /// <summary>"PNG", "JPEG", "GIF", "WebP", or the type as given.</summary>
    public static string Kind(string mediaType) => mediaType switch
    {
        "image/png" => "PNG",
        "image/jpeg" => "JPEG",
        "image/gif" => "GIF",
        "image/webp" => "WebP",
        _ => mediaType,
    };

    /// <summary>"image 1 (shot.png, 1280×720 PNG, 84 KB)": an image as a prompt or a list names it.</summary>
    public static string Label(ImageRef image, int number)
    {
        ArgumentNullException.ThrowIfNull(image);
        var name = Clean(image.Name);
        return string.Create(CultureInfo.InvariantCulture,
            $"image {number} ({(name is null ? "" : name + ", ")}{image.Width}×{image.Height} {Kind(image.MediaType)}, {Size(image.Bytes)})");
    }
}
