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
        // The window's first focus would land on the first button with a keyboard ring: the page takes it instead, and
        // hears Esc from anywhere in it.
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        KeyDown += OnRootKeyDown;
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
        Detach();
        ViewModel = ((AppComposition)e.Parameter).Gateway;
        Bindings.Update();
        ViewModel.PropertyChanged += OnViewModelChanged;
        // A link (the Board's "Pair as owner…") is handled before the page exists: its card comes into view now.
        if (ViewModel.PendingLanding is not null)
            LandOnCard();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Detach();
        base.OnNavigatedFrom(e);
    }

    private void Detach()
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GatewayViewModel.PendingLanding) && ViewModel.PendingLanding is not null)
            LandOnCard();
    }

    /// <summary>
    /// Brings the linked request's card into view once it is laid out, and focuses the owner question's answer when the
    /// link opened it (so Enter answers and Esc cancels), else the card's first answer.
    /// </summary>
    private void LandOnCard()
    {
        if (ViewModel.TakePendingLanding() is not { } card)
            return;
        var passes = 0;
        var done = false;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (done)
                return;
            if (RequestCards.ContainerFromItem(card) is FrameworkElement { ActualHeight: > 0 } container)
            {
                done = true;
                RequestCards.LayoutUpdated -= handler;
                container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                var label = card.IsConfirmingOwner ? "Pair as owner" : card.CanPair ? "Pair as guest" : "Ignore";
                OpsLayout.FocusWhenShown(container, () => Find(container, label), FocusState.Keyboard);
            }
            else if (++passes > 60)
            {
                RequestCards.LayoutUpdated -= handler;
            }
        };
        RequestCards.LayoutUpdated += handler;
        DispatcherQueue.TryEnqueue(() => handler(null, EventArgs.Empty));
    }

    private void Arrange() =>
        OpsLayout.Arrange(ActualWidth, HeadHost, HeadTitle, HeadButtons, Body, Columns, RightColumn, RightStack);

    private void OnTableSized(object sender, SizeChangedEventArgs e) => OpsLayout.FitTable(sender, e);

    /// <summary>The owner question's answer takes focus when it appears, so Enter answers and Esc cancels.</summary>
    private void OnAskOwnerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement ask)
            return;
        DependencyObject? parent = ask;
        while (parent is not null and not Border { Tag: "request" })
            parent = VisualTreeHelper.GetParent(parent);
        if (parent is not Border card)
            return;
        OpsLayout.FocusWhenShown(card, () => Find(card, "Pair as owner"), FocusState.Keyboard);
    }

    private static Button? Find(DependencyObject parent, string label)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button { Content: string content } button && content == label)
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
