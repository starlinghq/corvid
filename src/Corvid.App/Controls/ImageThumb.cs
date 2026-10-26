using Corvid.App.Core.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Corvid.App.Controls;

/// <summary>
/// K-8 (A6): a message image in the transcript, bounded (<see cref="ImageView.DisplayWidth"/> by
/// <see cref="ImageView.DisplayHeight"/>, at most 360 by 240) and decoded at that size, so a large picture never holds its
/// full bitmap in the window. The bytes come from the session service by digest; they are untrusted, and only the image
/// decoder reads them. A local image only: nothing here fetches anything (plan §4.2).
/// </summary>
public sealed partial class ImageThumb : Grid
{
    public static readonly DependencyProperty ViewProperty =
        DependencyProperty.Register(nameof(View), typeof(ImageView), typeof(ImageThumb), new PropertyMetadata(null, (d, _) => ((ImageThumb)d).Load()));

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _missing = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private int _generation;

    public ImageThumb()
    {
        Children.Add(_image);
        Children.Add(_missing);
        CornerRadius = new CornerRadius(6);
    }

    public ImageView? View
    {
        get => (ImageView?)GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    private async void Load()
    {
        var generation = ++_generation;
        _image.Source = null;
        _missing.Text = "";
        if (View is not { } view)
            return;
        Width = view.DisplayWidth;
        Height = view.DisplayHeight;
        try
        {
            var bytes = await view.LoadAsync().ConfigureAwait(true);
            if (generation != _generation)
                return;
            if (bytes is null)
            {
                _missing.Text = "image not stored";
                return;
            }
            var bitmap = new BitmapImage
            {
                // Decoded at the drawn size (twice it for a sharp result on a scaled display), never at full size.
                DecodePixelWidth = (int)Math.Ceiling(view.DisplayWidth * 2),
                DecodePixelType = DecodePixelType.Logical,
            };
            using var stream = new MemoryStream(bytes, writable: false);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream()).AsTask().ConfigureAwait(true);
            if (generation == _generation)
                _image.Source = bitmap;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _generation)
                _missing.Text = "image could not be shown";
            Console.Error.WriteLine("corvid app: an image could not be shown: " + exception.Message);
        }
    }
}
