using System.ComponentModel;
using Corvid.App.Core.Audit;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Shell;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Audit area's page (Audit.png, Audit-narrow.png): the event log of the picked session with its filters, the
/// selected event (the egress ledger for a request that left the machine, the raw event otherwise), the session's
/// detection and trace export. The view model does the work; this class owns what only a view can: the session menu,
/// the clipboard, focus, scrolling and the head's wrapping.
/// <para>
/// Keys: Up and Down in the Events table move the selection (and focus) between rows, Enter and Space open the focused
/// row, Ctrl+F puts the caret in the "path or tool" filter (ui.md §6's find), and Esc first shows every event again
/// after a detection's link narrowed the log, then closes the open event.
/// </para>
/// </summary>
public sealed partial class AuditPage : Page
{
    private AppComposition? _composition;

    public AuditPage()
    {
        InitializeComponent();
        Root.KeyDown += OnRootKeyDown;
        var find = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        find.Invoked += (_, args) =>
        {
            PathBox.Focus(FocusState.Keyboard);
            PathBox.SelectAll();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(find);
    }

    public AuditViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _composition = (AppComposition)e.Parameter;
        ViewModel = _composition.Audit;
        ViewModel.PropertyChanged += OnViewModelChanged;
        _composition.Shell.PropertyChanged += OnShellChanged;
        ApplyLayout();
        Bindings.Update();
        UpdateSessionLabel();
        _ = DispatcherQueue.TryEnqueue(BringSelectionIntoView);
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        if (_composition is not null)
            _composition.Shell.PropertyChanged -= OnShellChanged;
        base.OnNavigatedFrom(e);
    }

    // ---- x:Bind helpers ----

    /// <summary>The request as sent: .tail's 170 px for the excerpt, more once the whole request is open.</summary>
    public static double SentHeight(bool full) => full ? 360 : 170;

    public static string EventLinkName(string sequence) => $"Event {sequence}: show the detection's events with it open";

    // ---- The view model ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AuditViewModel.SessionIndex) or nameof(AuditViewModel.SessionId):
                UpdateSessionLabel();
                break;
            case nameof(AuditViewModel.SelectedRow):
                ResetCopyButtons();
                _ = DispatcherQueue.TryEnqueue(BringSelectionIntoView);
                break;
        }
    }

    private void UpdateSessionLabel()
    {
        var index = ViewModel.SessionIndex;
        SessionLabel.Text = index >= 0 && index < ViewModel.SessionOptions.Count ? ViewModel.SessionOptions[index].Label : "";
        AutomationProperties.SetName(SessionPicker, $"Session: {SessionLabel.Text}");
    }

    private void OnSessionMenuOpening(object? sender, object e)
    {
        SessionMenu.Items.Clear();
        for (var i = 0; i < ViewModel.SessionOptions.Count; i++)
        {
            var index = i;
            var item = new ToggleMenuFlyoutItem
            {
                Text = ViewModel.SessionOptions[i].Label,
                IsChecked = i == ViewModel.SessionIndex,
            };
            item.Click += (_, _) => ViewModel.SessionIndex = index;
            SessionMenu.Items.Add(item);
        }
    }

    // ---- Actions ----

    /// <summary>"Export trace…": the Export panel, brought into view with its first choice focused.</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        ExportPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        FullRadio.Focus(FocusState.Programmatic);
    }

    private void OnCopySentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Egress is { } egress && Copy(egress.Text))
            CopySentButton.Content = "Copied";
    }

    private void OnCopyRawClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Detail is { } detail && Copy(detail.Json))
            CopyRawButton.Content = "Copied";
    }

    private void ResetCopyButtons()
    {
        CopySentButton.Content = "Copy";
        CopyRawButton.Content = "Copy";
    }

    private static bool Copy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or NotImplementedException)
        {
            // A head without a clipboard: the text stays selectable in the block.
            return false;
        }
    }

    // ---- Keys and focus ----

    private void OnRowsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var delta = e.Key switch
        {
            VirtualKey.Down => 1,
            VirtualKey.Up => -1,
            _ => 0,
        };
        if (delta == 0)
            return;
        // From the focused row, not the selected one: Tab may have moved focus without opening anything.
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Button { DataContext: AuditEventRow focused } && !ReferenceEquals(focused, ViewModel.SelectedRow))
            ViewModel.Select(focused);
        ViewModel.MoveSelection(delta);
        FocusSelectedRow();
        e.Handled = true;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || e.Handled)
            return;
        if (ViewModel.HasMatch)
        {
            ViewModel.ClearMatchCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel.SelectedRow is not null)
        {
            ViewModel.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private Button? RowButton(AuditEventRow row) =>
        EventRows.ContainerFromItem(row) is DependencyObject container ? FindButton(container) : null;

    private void FocusSelectedRow()
    {
        if (ViewModel.SelectedRow is { } row && RowButton(row) is { } button)
        {
            button.Focus(FocusState.Keyboard);
            button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    /// <summary>A link that landed here (a detection's events, the latest egress) shows its row, however far down.</summary>
    private void BringSelectionIntoView()
    {
        if (ViewModel?.SelectedRow is { } row && RowButton(row) is { } button)
            button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.3 });
    }

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button match)
            return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindButton(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    // ---- Width ----

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Layout))
            ApplyLayout();
    }

    /// <summary>
    /// Three columns down to the narrow layout (Audit-narrow.png keeps them, the table clipped and scrolling sideways);
    /// on the phone the panels stack in reading order with 14 px gutters.
    /// </summary>
    private void ApplyLayout()
    {
        var phone = _composition?.Shell.Layout == ShellLayout.Phone;
        FilterColumn.Width = phone ? new GridLength(1, GridUnitType.Star) : new GridLength(200);
        EventsColumn.Width = phone ? new GridLength(0) : new GridLength(1.3, GridUnitType.Star);
        SideColumn.Width = phone ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Body.ColumnSpacing = phone ? 0 : 18;
        Place(FilterPanel, 0, 0);
        Place(EventsPanel, phone ? 1 : 0, phone ? 0 : 1);
        Place(SideStack, phone ? 2 : 0, phone ? 0 : 2);
        Body.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 18, 24, 18);
        Head.Padding = phone ? new Thickness(14, 10, 14, 10) : new Thickness(24, 12, 24, 12);
    }

    private static void Place(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    /// <summary>The head's two groups share a line while they fit (flex-wrap: wrap); otherwise the actions go below.</summary>
    private void OnHeadSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        HeadLeft.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        HeadActions.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var fits = HeadLeft.DesiredSize.Width + HeadGrid.ColumnSpacing + HeadActions.DesiredSize.Width <= width + 0.5;
        var row = fits ? 0 : 1;
        var column = fits ? 1 : 0;
        if (Grid.GetRow(HeadActions) == row && Grid.GetColumn(HeadActions) == column)
            return;
        Grid.SetRow(HeadActions, row);
        Grid.SetColumn(HeadActions, column);
        HeadActions.HorizontalAlignment = HorizontalAlignment.Left;
    }
}
