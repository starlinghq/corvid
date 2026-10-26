using System.Security.Cryptography;
using Corvid.Abstractions.IO;
using Corvid.Abstractions.Media;

namespace Corvid.Kernel.Media;

/// <summary>
/// K-8 (A6): images by SHA-256, as files beside the event log (<c>media/sha256/ab/&lt;digest&gt;</c>). Content-addressed, so
/// writing is idempotent, a fork's copied events find their images, and nothing is ever rewritten; nothing is deleted
/// either (a rewound or compacted message's images stay, as its event does). The directory is owner only, like the rest of
/// the data directory. A read checks the bytes against their name, so a file changed on disk is not sent as the image.
/// </summary>
public sealed class MediaStore : IMediaStore
{
    public MediaStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string Put(ReadOnlySpan<byte> data)
    {
        var digest = Digest(data);
        var path = PathOf(digest);
        if (File.Exists(path) && new FileInfo(path).Length == data.Length)
            return digest;
        PrivateDirectory.Create(Root);
        PrivateDirectory.Create(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another writer stored the same bytes first: content-addressed, so theirs is ours.
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return digest;
    }

    public byte[]? Read(string digest)
    {
        if (!IsDigest(digest))
            return null;
        var path = PathOf(digest);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        return Digest(bytes) == digest ? bytes : null;
    }

    /// <summary>The SHA-256 of the bytes, 64 lowercase hex digits.</summary>
    public static string Digest(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Whether a string is a digest this store names files by (so a crafted one cannot reach another path).</summary>
    public static bool IsDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string PathOf(string digest) => Path.Combine(Root, "sha256", digest[..2], digest);
}

/// <summary>K-8 (A6): images as a message takes them in: checked, stripped of metadata and stored, or refused with why.</summary>
public static class ImageIngest
{
    /// <summary>
    /// Checks every image (at most <see cref="Models.Images.ImageFiles.MaxPerMessage"/>; each read from its bytes, within the
    /// size limits) and stores the stripped bytes; nothing is stored when any is refused.
    /// </summary>
    /// <exception cref="ImageRefusedException">Why the images are refused, for the person.</exception>
    public static System.Collections.Immutable.ImmutableArray<ImageRef> Store(IMediaStore store, IReadOnlyList<ImageInput> images)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count > Models.Images.ImageFiles.MaxPerMessage)
            throw new ImageRefusedException($"A message may carry at most {Models.Images.ImageFiles.MaxPerMessage} images; this one has {images.Count}.");
        var prepared = images.Select(image => (Image: image, Prepared: Models.Images.ImageFiles.Prepare(image.Data, image.Name))).ToList();
        return [.. prepared.Select(item => new ImageRef(store.Put(item.Prepared.Data), item.Prepared.Info.MediaType, item.Prepared.Data.Length,
            item.Prepared.Info.Width, item.Prepared.Info.Height, ImageNames.Clean(item.Image.Name)))];
    }
}
