using System.ComponentModel;
using Corvid.App.Core.Shell;

namespace Corvid.App.Pages;

/// <summary>
/// The Sessions page's width class, for its templates: phone (Phone.png: the compact approval card with full-width
/// answers) or narrow (Main-narrow.png: 14 px gutters). Templates bind to <see cref="Current"/>; the page sets it from the
/// shell's layout.
/// </summary>
public sealed class SessionsLayout : INotifyPropertyChanged
{
    private ShellLayout _layout = ShellLayout.Wide;

    public static SessionsLayout Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public ShellLayout Layout
    {
        get => _layout;
        set
        {
            if (_layout == value)
                return;
            _layout = value;
            // By name: x:Bind's static-rooted paths do not refresh on an empty property name.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Layout)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPhone)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNarrow)));
        }
    }

    /// <summary>Under 720 px: the phone layout.</summary>
    public bool IsPhone => _layout == ShellLayout.Phone;

    /// <summary>Under 860 px: the rail is gone and the gutters narrow.</summary>
    public bool IsNarrow => _layout is ShellLayout.Narrow or ShellLayout.Phone;
}
