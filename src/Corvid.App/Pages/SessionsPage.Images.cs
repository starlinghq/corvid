using Corvid.App.Core.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// K-8 (A6), flow F19: images into the composer. A pasted bitmap or copied image file, a file dropped on the composer and a
/// path typed in the attach box become chips (the view model checks them; the engine strips and stores them when sent). A
/// dropped file that is not an image becomes an <c>@</c> reference in the draft; a dropped folder is refused with why.
/// </summary>
public sealed partial class SessionsPage
{
    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        if (_session is { IsReadOnly: false } && (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap)))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Attach";
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (_session is not { } session)
            return;
        var deferral = e.GetDeferral();
        try
        {
            await TakeAsync(session, e.DataView).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            session.ReportAttachProblem("The drop could not be read: " + exception.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // A paste that holds an image (a bitmap, or copied image files) attaches it; text pastes as usual.
    private void OnComposerPaste(object sender, TextControlPasteEventArgs e)
    {
        if (ImagesOnClipboard() is not { } content)
            return;
        e.Handled = true;
        PasteImages(content.Session, content.View);
    }

    /// <summary>
    /// Ctrl+V in the composer when the clipboard holds an image or files and no text. Uno's TextBox raises
    /// <see cref="TextBox.Paste"/> only for a clipboard that holds text (the A6 end-to-end pass on X11: a copied PNG, or copied
    /// files as <c>text/uri-list</c>, raised no Paste at all), so the key takes them; text still pastes as the TextBox does it.
    /// </summary>
    private bool PasteImagesFromKey()
    {
        if (ImagesOnClipboard() is not { } content)
            return false;
        PasteImages(content.Session, content.View);
        return true;
    }

    private (SessionViewModel Session, DataPackageView View)? ImagesOnClipboard()
    {
        if (_session is not { CanAttachImages: true } session)
            return null;
        DataPackageView content;
        try
        {
            content = Clipboard.GetContent();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("corvid app: the clipboard could not be read: " + exception.Message);
            return null;
        }
        if (content.Contains(StandardDataFormats.Text)
            || !(content.Contains(StandardDataFormats.Bitmap) || content.Contains(StandardDataFormats.StorageItems) || RawImageFormat(content) is not null))
            return null;
        return (session, content);
    }

    // An image the clipboard holds under its media type rather than as the Bitmap format (Uno's X11 clipboard).
    private static string? RawImageFormat(DataPackageView view) => ImageSignature.ClipboardImageFormat(view.AvailableFormats);

    private async void PasteImages(SessionViewModel session, DataPackageView content)
    {
        try
        {
            await TakeAsync(session, content).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            session.ReportAttachProblem("The pasted image could not be read: " + exception.Message);
        }
    }

    private static async Task TakeAsync(SessionViewModel session, DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            foreach (var item in await view.GetStorageItemsAsync())
            {
                if (item is IStorageFolder)
                    session.ReportAttachProblem($"'{Core.SafeText.Clean(item.Name)}' is a folder: a folder cannot be attached.");
                else if (!string.IsNullOrEmpty(item.Path))
                    await session.AttachFileAsync(item.Path).ConfigureAwait(true);
            }
            return;
        }
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await view.GetBitmapAsync();
            using var stream = (await reference.OpenReadAsync()).AsStreamForRead();
            // Bounded: one byte past the limit is enough to refuse it.
            var bytes = await ReadBoundedAsync(stream, ImageSignature.MaxBytes + 1).ConfigureAwait(true);
            session.Attach(new ImageAttachment(bytes));
            return;
        }
        if (RawImageFormat(view) is { } format)
        {
            var data = await view.GetDataAsync(format);
            var bytes = data switch
            {
                byte[] raw => raw.Length > ImageSignature.MaxBytes ? raw[..(ImageSignature.MaxBytes + 1)] : raw,
                Stream stream => await ReadBoundedAsync(stream, ImageSignature.MaxBytes + 1).ConfigureAwait(true),
                IRandomAccessStream random => await ReadBoundedAsync(random.AsStreamForRead(), ImageSignature.MaxBytes + 1).ConfigureAwait(true),
                RandomAccessStreamReference reference => await ReadBoundedAsync((await reference.OpenReadAsync()).AsStreamForRead(), ImageSignature.MaxBytes + 1)
                    .ConfigureAwait(true),
                _ => null,
            };
            if (bytes is null)
                session.ReportAttachProblem($"The clipboard's {Core.SafeText.Clean(format)} could not be read as an image.");
            else
                session.Attach(new ImageAttachment(bytes));
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < limit && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length))).ConfigureAwait(true)) > 0)
            buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    private void OnAttachClick(object sender, RoutedEventArgs e) => AttachTyped();

    private void OnAttachPathKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        AttachTyped();
    }

    private async void AttachTyped()
    {
        var path = AttachPath.Text.Trim();
        if (_session is not { } session || path.Length == 0)
            return;
        AttachFlyout.Hide();
        AttachPath.Text = "";
        await session.AttachFileAsync(path, imagesOnly: true).ConfigureAwait(true);
        Composer.Focus(FocusState.Programmatic);
    }
}
