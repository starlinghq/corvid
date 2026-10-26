using System.Runtime.CompilerServices;
using Corvid.Abstractions.Media;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Images;

/// <summary>
/// K-8: an image of the log in a rendered prompt, before its bytes are read: the prompt renderer reads only the log, and the
/// turn replaces each one with the bytes (<see cref="DataContent"/>) for a generator that reads images, a description from
/// the vision slot, or a note saying why it is not shown. Never sent: <see cref="ImageCapabilityChatClient"/> turns any that
/// reaches a model into its note.
/// </summary>
public sealed class ImageReferenceContent(ImageRef image, int number) : AIContent
{
    public ImageRef Image { get; } = image ?? throw new ArgumentNullException(nameof(image));

    /// <summary>The image's number in the session's conversation, from 1, as its label says.</summary>
    public int Number { get; } = number;

    public string Label => ImageNames.Label(Image, Number);
}

/// <summary>K-8: the image parts of model requests and the notes that stand in for them.</summary>
public static class ImageContents
{
    /// <summary>The <see cref="AIContent.AdditionalProperties"/> key holding the image's label on a <see cref="DataContent"/> the turn built.</summary>
    public const string LabelKey = "corvid.image_label";

    /// <summary>The key holding the image's digest on a <see cref="DataContent"/> the turn built (for the egress ledger).</summary>
    public const string DigestKey = "corvid.image_digest";

    /// <summary>Whether a content is an image: one the log refers to, or image bytes (or an image URI) in the request.</summary>
    public static bool IsImage(AIContent content) => content switch
    {
        ImageReferenceContent => true,
        DataContent data => data.HasTopLevelMediaType("image"),
        UriContent uri => uri.HasTopLevelMediaType("image"),
        _ => false,
    };

    /// <summary>Whether any message holds an image.</summary>
    public static bool HasImages(IEnumerable<ChatMessage> messages) =>
        messages.Any(message => message.Contents.Any(IsImage));

    /// <summary>The bytes of a stored image as a request carries them, labelled for notes and the ledger.</summary>
    public static DataContent Data(ImageReferenceContent reference, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(bytes);
        return new DataContent(bytes, reference.Image.MediaType)
        {
            AdditionalProperties = new() { [LabelKey] = reference.Label, [DigestKey] = reference.Image.Digest },
        };
    }

    /// <summary>The label of an image content: the turn's, or a plain one for an image from elsewhere.</summary>
    public static string LabelOf(AIContent content) => content switch
    {
        ImageReferenceContent reference => reference.Label,
        _ when content.AdditionalProperties?.TryGetValue(LabelKey, out var label) == true && label is string text => text,
        DataContent data => $"an image ({ImageNames.Kind(data.MediaType)}, {ImageNames.Size(data.Data.Length)})",
        _ => "an image",
    };

    /// <summary>"[image 1 (shot.png, 1280×720 PNG, 84 KB): not shown, because …]": what a model that does not see an image reads.</summary>
    public static TextContent Note(AIContent image, string why) => new($"[{LabelOf(image)}: not shown, because {why}]");
}

/// <summary>
/// K-8: around every chat binding the host makes (beside the egress gate), so no model is sent an image it cannot read and
/// none is sent metadata. For a profile with <see cref="ModelProfile.Vision"/> image bytes pass, stripped of metadata again
/// (the store holds stripped bytes; this covers any other path); for any other profile each image becomes a note saying it
/// is not shown, so a fallback or escalation to a text-only model gets words, not bytes it would reject or ignore. An
/// <see cref="ImageReferenceContent"/> whose bytes the turn did not read becomes a note for every profile.
/// </summary>
public sealed class ImageCapabilityChatClient(IChatClient inner, ModelProfile profile) : DelegatingChatClient(inner)
{
    private readonly ModelProfile _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(Prepare(messages, _profile), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(Prepare(messages, _profile), options, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    /// <summary>The messages as <paramref name="profile"/> may receive them; the same list when they hold no image.</summary>
    public static IEnumerable<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(profile);
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (!HasAny(list))
            return list;
        return list.Select(message => message.Contents.Any(ImageContents.IsImage) ? Rewrite(message, profile) : message).ToList();
    }

    private static bool HasAny(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
            foreach (var content in message.Contents)
                if (ImageContents.IsImage(content))
                    return true;
        return false;
    }

    private static ChatMessage Rewrite(ChatMessage message, ModelProfile profile)
    {
        var copy = message.Clone();
        copy.Contents = [.. message.Contents.Select(content => Convert(content, profile))];
        return copy;
    }

    private static AIContent Convert(AIContent content, ModelProfile profile)
    {
        if (!ImageContents.IsImage(content))
            return content;
        if (content is ImageReferenceContent)
            return ImageContents.Note(content, "its bytes were not read for this request");
        if (!profile.Vision)
            return ImageContents.Note(content, $"this model ({profile.Id}) does not read images");
        if (content is UriContent)
            // A remote image is never fetched or forwarded (plan §4.2): fetching is egress, and the model would fetch it.
            return ImageContents.Note(content, "a remote image is never fetched or passed on");
        var data = (DataContent)content;
        byte[] stripped;
        try
        {
            stripped = ImageFiles.StripMetadata(data.Data.Span);
        }
        catch (InvalidDataException)
        {
            return ImageContents.Note(content, "it could not be read as a PNG, JPEG, GIF or WebP image");
        }
        var info = ImageFiles.Inspect(stripped);
        return new DataContent(stripped, info?.MediaType ?? data.MediaType)
        {
            AdditionalProperties = data.AdditionalProperties is null ? null : new(data.AdditionalProperties),
        };
    }
}
