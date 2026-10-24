using Corvid.App.Core.Composition;
using Corvid.App.Core.Settings;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Settings area's page (docs/plan/ui.md F24). Its view model comes from the composition the shell navigates with
/// and loads once; the page subscribes to nothing of it beyond x:Bind, so a visit leaves nothing behind. A key typed in
/// a keybinding's field is set with Enter or its Set button; Esc puts the field back. Under 720 px the gutters
/// narrow to the phone's.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private const double PhoneWidth = 720;

    public SettingsPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Arrange();
        // The window's first focus would land on a field in the middle of the page, with its caret: the page takes it.
        IsTabStop = true;
        Loaded += (_, _) =>
        {
            Arrange();
            Focus(FocusState.Programmatic);
        };
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
    }

    public SettingsViewModel ViewModel { get; private set; } = null!;

    /// <summary>The app's own preferences' note: kept for this run until ui.db keeps them.</summary>
    public static string PreferencesNote => "kept for this run · the window follows them once ui.db keeps them (G-7)";

    public static string OnOff(bool on) => on ? "On" : "Off";

    public static string Prefixed(string prefix, string text) => prefix + text;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Settings;
        Bindings.Update();
        // Failures show on the page as a red notice; the task itself never throws.
        _ = ViewModel.EnsureLoadedAsync();
        base.OnNavigatedTo(e);
    }

    /// <summary>The view model outlives the page: x:Bind lets go of its change notifications as the page goes.</summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Bindings.StopTracking();
        base.OnNavigatedFrom(e);
    }

    private void Arrange()
    {
        var phone = ActualWidth > 0 && ActualWidth < PhoneWidth;
        // The phone's top bar names the page already.
        PageTitle.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        Body.Padding = phone ? new Thickness(14, 14, 14, 18) : new Thickness(24, 18, 24, 24);
        Head.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 12, 24, 12);
        SaveBar.Padding = phone ? new Thickness(14, 8, 14, 8) : new Thickness(24, 8, 24, 8);
        NoticeHost.Padding = phone ? new Thickness(14, 10, 14, 0) : new Thickness(24, 12, 24, 0);
        // As wide as the panels under it (the body's 980 less its gutters), or the window's width less them.
        NoticeRow.Width = Math.Max(0, Math.Min(Body.MaxWidth, ActualWidth) - Body.Padding.Left - Body.Padding.Right);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not TextBox { DataContext: KeyBindingRowViewModel row })
            return;
        switch (e.Key)
        {
            case VirtualKey.Enter:
                row.Apply();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                row.EditText = row.KeyText;
                e.Handled = true;
                break;
        }
    }
}
