using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// K-8 (A6), flow F19: images in the composer. A pasted image, a dropped image file and one named in the attach box become
/// chips above the composer and go with the next message; a dropped file that is not an image becomes an <c>@</c>
/// reference in the draft. The composer checks the signature, the size and the count so a wrong file is said at once; the
/// engine checks again, strips the metadata and stores the image, or refuses the message, whose text and chips then come back.
/// </summary>
public sealed partial class SessionViewModel
{
    /// <summary>The images that go with the next message.</summary>
    public ObservableCollection<AttachmentChip> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Whether this service takes images (the engine does; the demo does not).</summary>
    public bool CanAttachImages => _service.CanSendImages && !IsReadOnly;

    /// <summary>Says why a paste or a drop could not be taken (the page reads the clipboard and the drop).</summary>
    public void ReportAttachProblem(string message) => Error = message ?? "";

    /// <summary>Reads a message image's bytes for its thumbnail, through the service.</summary>
    public Task<byte[]?> ReadImageAsync(string digest, CancellationToken cancellationToken = default) =>
        _service.ReadImageAsync(SessionId, digest, cancellationToken);

    /// <summary>
    /// Adds an image for the next message (a paste): refused, with why in <see cref="Error"/>, when the service takes no
    /// images, it is not a PNG, JPEG, GIF or WebP image, it is too large, or the message has as many as it may carry.
    /// </summary>
    public bool Attach(ImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var name = image.Name is { Length: > 0 } given ? $"'{SafeText.Clean(Path.GetFileName(given))}'" : "The pasted image";
        string? refusal = !CanAttachImages ? (IsReadOnly ? "This session is read-only." : "Images in messages come with the engine (K-8).")
            : image.Data.Length == 0 ? $"{name} is empty."
            : image.Data.Length > ImageSignature.MaxBytes ? $"{name} is {ImageSignature.Size(image.Data.Length)}; an image may be at most {ImageSignature.Size(ImageSignature.MaxBytes)}."
            : image.MediaType is null ? $"{name} is not a PNG, JPEG, GIF or WebP image."
            : Attachments.Count >= ImageSignature.MaxPerMessage ? $"A message may carry at most {ImageSignature.MaxPerMessage} images."
            : null;
        if (refusal is not null)
        {
            Error = refusal;
            return false;
        }
        Error = "";
        AddChip(image);
        return true;
    }

    /// <summary>
    /// A dropped file or one named in the attach box: an image file is read through the service (its rules: no credential
    /// file, no protected location, the size checked first) and attached; any other file is named in the draft as an
    /// <c>@</c> reference for the model to read with its tools (<paramref name="imagesOnly"/>: the attach box takes images only).
    /// </summary>
    public async Task AttachFileAsync(string path, bool imagesOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsReadOnly)
        {
            Error = "This session is read-only.";
            return;
        }
        if (!ImageSignature.HasImageExtension(path))
        {
            if (imagesOnly)
            {
                Error = $"'{SafeText.Clean(Path.GetFileName(path))}' is not a PNG, JPEG, GIF or WebP file.";
                return;
            }
            var reference = path.Contains(' ', StringComparison.Ordinal) ? $"@\"{path}\"" : "@" + path;
            Draft = _draft.Length == 0 ? reference + " " : _draft.EndsWith(' ') ? _draft + reference + " " : $"{_draft} {reference} ";
            return;
        }
        if (!CanAttachImages)
        {
            Error = "Images in messages come with the engine (K-8).";
            return;
        }
        try
        {
            Attach(await _service.ReadImageFileAsync(SessionId, path).ConfigureAwait(true));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = exception.Message;
        }
    }

    private void AddChip(ImageAttachment image)
    {
        AttachmentChip? chip = null;
        chip = new AttachmentChip(image, new RelayCommand(() => RemoveAttachment(chip!)));
        Attachments.Add(chip);
        OnPropertyChanged(nameof(HasAttachments));
        RefreshComposerCommands();
    }

    private void RemoveAttachment(AttachmentChip chip)
    {
        if (Attachments.Remove(chip))
        {
            OnPropertyChanged(nameof(HasAttachments));
            RefreshComposerCommands();
        }
    }

    /// <summary>The chips' images, taken out of the composer for sending.</summary>
    private ImmutableArray<ImageAttachment> TakeAttachments()
    {
        var images = Attachments.Select(chip => chip.Image).ToImmutableArray();
        if (images.IsEmpty)
            return images;
        Attachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
        return images;
    }

    /// <summary>A refused message's images back above the composer, before any added since.</summary>
    private void GiveBackImages(ImmutableArray<ImageAttachment> images)
    {
        if (images.IsDefaultOrEmpty)
            return;
        var later = Attachments.Select(chip => chip.Image).ToList();
        Attachments.Clear();
        foreach (var image in images.Concat(later))
            AddChip(image);
    }
}
