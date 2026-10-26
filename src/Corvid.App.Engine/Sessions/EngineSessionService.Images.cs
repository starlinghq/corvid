using System.Collections.Immutable;
using Corvid.Abstractions.Media;
using Corvid.App.Core.Sessions;
using Corvid.Host.Sessions;
using Corvid.Kernel.Media;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// K-8 (A6), flow F19: messages with images. The composer's images are checked before they are queued (so a refusal gives
/// the person their message and chips back with why), queued with the message, and posted with it; the turn checks them
/// again, strips their metadata and stores them in the runtime's media store, whose bytes the transcript's thumbnails read.
/// </summary>
public sealed partial class EngineSessionService
{
    public bool CanSendImages => true;

    /// <summary>
    /// Sends a message with images, queued behind a running turn as Enter queues. Refused before anything is queued when no
    /// model could read the images (the generator does not and no vision model is set) or one is not an image, too large,
    /// or there are too many.
    /// </summary>
    public async Task SendAsync(string sessionId, string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            await QueueAsync(sessionId, text, cancellationToken).ConfigureAwait(false);
            return;
        }
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Writable(channel);
        var session = await SessionAsync(runtime, sessionId, cancellationToken).ConfigureAwait(false);
        var inputs = EngineImages.Inputs(images);
        // Reading every image's structure is CPU work over up to 8 × 20 MiB: off the UI thread.
        if (await Task.Run(() => session.CheckImages(inputs), cancellationToken).ConfigureAwait(false) is { } refusal)
            throw new InvalidOperationException(refusal);
        await EnqueueAsync(sessionId, text, modeLines: true, cancellationToken, [.. images]).ConfigureAwait(false);
    }

    /// <summary>A dropped or named image file, read under the host's rules (<see cref="ImageAttachments.FromPath"/>).</summary>
    public async Task<ImageAttachment> ReadImageFileAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        try
        {
            var image = await Task.Run(() => ImageAttachments.FromPath(path, ImageAttachments.EnvironmentFor(runtime, sessionId)), cancellationToken).ConfigureAwait(false);
            return new ImageAttachment(image.Data, image.Name);
        }
        catch (ImageRefusedException refused)
        {
            throw new InvalidOperationException(refused.Message);
        }
    }

    /// <summary>A message image's bytes from the media store, checked against their digest; null when none are stored.</summary>
    public async Task<byte[]?> ReadImageAsync(string sessionId, string digest, CancellationToken cancellationToken = default)
    {
        if (!MediaStore.IsDigest(digest))
            return null;
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        return await Task.Run(() => runtime.Media.Read(digest), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The composer's images as the host takes them.</summary>
internal static class EngineImages
{
    public static ImmutableArray<ImageInput> Inputs(IEnumerable<ImageAttachment> images) =>
        [.. images.Select(image => new ImageInput(image.Data, image.Name))];

    /// <summary>The log's image references as the app's transcript draws them.</summary>
    public static ImmutableArray<MessageImage> Views(ImmutableArray<ImageRef> images) =>
        images.IsDefaultOrEmpty ? [] : [.. images.Select(image => new MessageImage(image.Digest, image.MediaType, image.Bytes, image.Width, image.Height, image.Name))];
}
