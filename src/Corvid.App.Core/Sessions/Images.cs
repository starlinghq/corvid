using System.Globalization;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// K-8 (A6): an image the composer holds for the next message (a paste, a drop, the attach box): its bytes as they came and
/// the file name, if any. Untrusted: the engine reads the format from the bytes, strips the metadata and stores it, or
/// refuses the message; the composer only checks the signature so a wrong file is said at once.
/// </summary>
public sealed record ImageAttachment(byte[] Data, string? Name = null)
{
    public byte[] Data { get; } = Data ?? throw new ArgumentNullException(nameof(Data));

    /// <summary>"image/png", "image/jpeg", "image/gif" or "image/webp" from the signature; null for anything else.</summary>
    public string? MediaType => ImageSignature.MediaType(Data);
}

/// <summary>
/// K-8 (A6): an image a message carried, as the log refers to it; the bytes are the service's
/// (<see cref="ISessionService.ReadImageAsync"/>, by digest).
/// </summary>
/// <param name="Name">The file name it came with, or null for a paste. Outside text: shown cleaned.</param>
public sealed record MessageImage(string Digest, string MediaType, long Bytes, int Width, int Height, string? Name = null);

/// <summary>The image signatures the app knows (the engine checks the whole file; this only says "not an image" early).</summary>
public static class ImageSignature
{
    /// <summary>The most bytes the composer takes for one image (the engine's limit, 20 MiB).</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>The most images one message may carry.</summary>
    public const int MaxPerMessage = 8;

    public static string? MediaType(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF ? "image/jpeg"
        : data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
        : data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    /// <summary>"PNG", "JPEG", "GIF", "WebP".</summary>
    public static string Kind(string? mediaType) => mediaType switch
    {
        "image/png" => "PNG",
        "image/jpeg" => "JPEG",
        "image/gif" => "GIF",
        "image/webp" => "WebP",
        _ => "image",
    };

    /// <summary>Whether a file name has an image extension (a dropped file is attached as an image only when its bytes agree).</summary>
    public static bool HasImageExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    /// <summary>"84 KB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };
}

/// <summary>An image chip above the composer: "shot.png · PNG · 84 KB" and its ×.</summary>
public sealed class AttachmentChip
{
    internal AttachmentChip(ImageAttachment image, RelayCommand remove)
    {
        Image = image;
        RemoveCommand = remove;
        var name = image.Name is { Length: > 0 } given ? SafeText.Clean(Path.GetFileName(given)) : "pasted image";
        Label = $"{name} · {ImageSignature.Kind(image.MediaType)} · {ImageSignature.Size(image.Data.Length)}";
    }

    public ImageAttachment Image { get; }

    public string Label { get; }

    public RelayCommand RemoveCommand { get; }

    public string RemoveName => $"Remove the image {Label}";

    public string AutomationName => $"image to send: {Label}";
}

/// <summary>
/// An image in the transcript: a bounded thumbnail (at most <see cref="MaxWidth"/> by <see cref="MaxHeight"/>, the aspect
/// kept) and its caption "shot.png · 1280×720 PNG · 84 KB". The bytes are read through the service when it is drawn.
/// </summary>
public sealed class ImageView
{
    public const double MaxWidth = 360;
    public const double MaxHeight = 240;

    private readonly Func<CancellationToken, Task<byte[]?>> _load;

    /// <param name="load">Reads the image's bytes (the session service's <see cref="ISessionService.ReadImageAsync"/>).</param>
    public ImageView(MessageImage image, Func<CancellationToken, Task<byte[]?>> load)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
        Image = image;
        var name = image.Name is { Length: > 0 } given ? SafeText.Clean(given) : "image";
        Caption = string.Create(CultureInfo.InvariantCulture,
            $"{name} · {image.Width}×{image.Height} {ImageSignature.Kind(image.MediaType)} · {ImageSignature.Size(image.Bytes)}");
        var scale = Math.Min(1, Math.Min(MaxWidth / Math.Max(1, image.Width), MaxHeight / Math.Max(1, image.Height)));
        DisplayWidth = Math.Max(1, Math.Round(image.Width * scale));
        DisplayHeight = Math.Max(1, Math.Round(image.Height * scale));
    }

    public MessageImage Image { get; }

    /// <summary>The image's bytes for its thumbnail, or null when the service has none; untrusted, decoded bounded by the view.</summary>
    public Task<byte[]?> LoadAsync(CancellationToken cancellationToken = default) => _load(cancellationToken);

    public string Caption { get; }

    /// <summary>The thumbnail's size: the image's, scaled down to fit the bounds.</summary>
    public double DisplayWidth { get; }

    public double DisplayHeight { get; }

    public string AutomationName => $"image: {Caption}";
}
