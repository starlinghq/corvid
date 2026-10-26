using Corvid.App.Core.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
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
    private async void OnComposerPaste(object sender, TextControlPasteEventArgs e)
    {
        if (_session is not { CanAttachImages: true } session)
            return;
        DataPackageView content;
        try
        {
            content = Clipboard.GetContent();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("corvid app: the clipboard could not be read: " + exception.Message);
            return;
        }
        if (content.Contains(StandardDataFormats.Text) || !(content.Contains(StandardDataFormats.Bitmap) || content.Contains(StandardDataFormats.StorageItems)))
            return;
        e.Handled = true;
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
