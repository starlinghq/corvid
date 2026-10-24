using Corvid.App.Core.Composition;
using Corvid.App.Core.Gateway;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Gateway area's page. Its view model comes from the composition the shell navigates with. The page arranges itself
/// for the width (<see cref="OpsLayout"/>), closes a question or an allowlist editor with Esc, and focuses the answer the
/// owner question offers.
/// </summary>
public sealed partial class GatewayPage : Page
{
    public GatewayPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Arrange();
        Loaded += (_, _) => Arrange();
        Root.KeyDown += OnRootKeyDown;
    }

    public GatewayViewModel ViewModel { get; private set; } = null!;

    /// <summary>"Code A B C D - E F G H": the code read out one character at a time.</summary>
    public static string CodeName(string code) => "Code " + string.Join(' ', code.ToCharArray());

    /// <summary>"since today 08:02", or nothing for a channel that is off.</summary>
    public static string SinceText(string since) => since.Length == 0 ? "" : "since " + since;

    /// <summary>Tool names in the code font; "none: it can only talk" and an owner's line in the prose font.</summary>
    public static FontFamily ToolsFont(bool empty, bool guest) =>
        (FontFamily)Application.Current.Resources[guest && !empty ? "CorvidMono" : "CorvidSans"];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Gateway;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    private void Arrange() =>
        OpsLayout.Arrange(ActualWidth, HeadHost, HeadTitle, HeadButtons, Body, Columns, RightColumn, RightStack);

    private void OnTableSized(object sender, SizeChangedEventArgs e) => OpsLayout.FitTable(sender, e);

    /// <summary>The owner question's answer takes focus when it appears, so Enter answers and Esc cancels.</summary>
    private void OnAskOwnerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement ask)
            return;
        DependencyObject? card = ask;
        while (card is not null and not Border { Tag: "request" })
            card = VisualTreeHelper.GetParent(card);
        if (card is null)
            return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Find(card, "Pair as owner") is { } answer)
                answer.Focus(FocusState.Programmatic);
        });
    }

    private static Button? Find(DependencyObject parent, string label)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button { Content: string content, Visibility: Visibility.Visible } button && content == label && button.IsLoaded)
                return button;
            if (Find(child, label) is { } found)
                return found;
        }

        return null;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !e.Handled && ViewModel.Close())
            e.Handled = true;
    }
}
