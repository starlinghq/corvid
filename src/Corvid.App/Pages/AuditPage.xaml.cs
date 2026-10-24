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

    /// <summary>How long typing in the "path or tool" filter pauses before the table follows it.</summary>
    private static readonly TimeSpan PathFilterDelay = TimeSpan.FromMilliseconds(180);

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _pathTimer;

    public AuditPage()
    {
        InitializeComponent();
        _pathTimer = DispatcherQueue.CreateTimer();
        _pathTimer.Interval = PathFilterDelay;
        _pathTimer.IsRepeating = false;
        _pathTimer.Tick += (_, _) => ApplyPathFilter();
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
        ApplyPathFilter();
        ViewModel.PropertyChanged -= OnViewModelChanged;
        if (_composition is not null)
            _composition.Shell.PropertyChanged -= OnShellChanged;
        base.OnNavigatedFrom(e);
    }

    // ---- x:Bind helpers ----

    /// <summary>The request as sent: .tail's 170 px for the excerpt, more once the whole request is open.</summary>
    public static double SentHeight(bool full) => full ? 360 : 170;

    /// <summary>The table's time column: 72 px, none on the phone.</summary>
    public static GridLength TimeWidth(bool phone) => new(phone ? 0 : 72);

    public static string EventLinkName(string sequence) => $"Event {sequence}: show the detection's events with it open";

    // ---- The view model ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AuditViewModel.SessionIndex) or nameof(AuditViewModel.SessionId):
                UpdateSessionLabel();
                break;
            case nameof(AuditViewModel.ExportResult) when ViewModel.HasExportResult:
                // The result appears below the button, maybe under the window's edge: show it.
                _ = DispatcherQueue.TryEnqueue(() => ExportToast.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
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

    // ---- The "path or tool" filter: typing waits for a pause, so the table is not redone at every key ----

    private void OnPathTextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.Equals(PathBox.Text, ViewModel.PathFilter, StringComparison.Ordinal))
        {
            // The view model set it (reset, a link): nothing to wait for.
            _pathTimer.Stop();
            return;
        }

        _pathTimer.Stop();
        _pathTimer.Start();
    }

    private void ApplyPathFilter()
    {
        _pathTimer.Stop();
        if (ViewModel is not null && !string.Equals(PathBox.Text, ViewModel.PathFilter, StringComparison.Ordinal))
            ViewModel.PathFilter = PathBox.Text;
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

    /// <summary>
    /// Opening an event with the pointer when the right column's panels sit under the table (1024 wide, the phone): show
    /// the opened event, since it is not beside the row.
    /// </summary>
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (Grid.GetColumn(SideStack) == 2)
            return;
        // At low priority: the click's focus first brings the row into view, and this must come after it.
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var top = SideStack.TransformToVisual(Body).TransformPoint(default).Y;
            BodyScroll.ChangeView(null, Math.Max(0, top - 12), null, true);
        });
    }

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

    /// <summary>
    /// A link that landed here (a detection's events, the latest egress) shows its row when it is out of sight; a row in
    /// view stays where it is, so the page opens at its top as Audit.png does.
    /// </summary>
    private void BringSelectionIntoView()
    {
        if (ViewModel?.SelectedRow is not { } row || RowButton(row) is not { } button || button.ActualHeight <= 0)
            return;
        var top = button.TransformToVisual(BodyScroll).TransformPoint(default).Y;
        if (top >= 0 && top + button.ActualHeight <= BodyScroll.ViewportHeight)
            return;
        button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.3 });
    }

    /// <summary>
    /// The table fills the panel and wraps its summaries; below 460 px it keeps that width and scrolls sideways, as
    /// Audit-narrow.png's table runs past its panel.
    /// </summary>
    private void OnTableScrollSizeChanged(object sender, SizeChangedEventArgs e) =>
        Table.Width = Math.Max(TableMinWidth, e.NewSize.Width);

    private static double TableMinWidth => AuditLayout.Current.IsPhone ? 0 : 460;

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

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    /// <summary>
    /// Three columns while the page is at least 800 wide (Audit.png, and Audit-narrow.png at 820, where the table runs
    /// past its panel and scrolls sideways). Narrower beside the rail (1024 wide), the right column's panels go under the
    /// table, which keeps its width; on the phone everything stacks in reading order with 14 px gutters.
    /// </summary>
    private void ApplyLayout()
    {
        var phone = _composition?.Shell.Layout == ShellLayout.Phone;
        var width = BodyScroll.ActualWidth;
        var three = !phone && (width <= 0 || width >= ThreeColumnWidth);
        var star = new GridLength(1, GridUnitType.Star);
        FilterColumn.Width = phone ? star : new GridLength(200);
        EventsColumn.Width = phone ? new GridLength(0) : three ? new GridLength(1.3, GridUnitType.Star) : star;
        SideColumn.Width = three ? star : new GridLength(0);
        Body.ColumnSpacing = phone ? 0 : 18;
        Place(FilterPanel, 0, 0);
        Place(EventsPanel, phone ? 1 : 0, phone ? 0 : 1);
        Place(SideStack, phone ? 2 : three ? 0 : 1, phone ? 0 : three ? 2 : 1);
        Body.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 18, 24, 18);
        Head.Padding = phone ? new Thickness(14, 10, 14, 10) : new Thickness(24, 12, 24, 12);
        // The phone's top bar has the title; its table drops the time and wraps its summaries in the screen's width.
        TitleText.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        if (AuditLayout.Current.IsPhone != phone)
        {
            AuditLayout.Current.IsPhone = phone;
            // Rows made before the switch keep their columns (a template's static-rooted x:Bind is read when the row is
            // made), so make them again.
            if (ViewModel is not null)
            {
                EventRows.ItemsSource = null;
                EventRows.ItemsSource = ViewModel.Rows;
            }
        }
        HeadTimeColumn.Width = AuditLayout.Current.TimeWidth;
        HeadTimeCell.Visibility = AuditLayout.Current.TimeVisibility;
        Table.Width = Math.Max(TableMinWidth, TableScroll.ActualWidth);
    }

    private const double ThreeColumnWidth = 800;

    /// <summary>
    /// The egress panel's actions: the note sits beside the two buttons while it has room for a few words a line (it
    /// wraps there in Audit.png), and goes under them when the column is narrow.
    /// </summary>
    private void OnSentActionsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var below = e.NewSize.Width - SentToggle.ActualWidth - CopySentButton.ActualWidth - 16 < 110;
        Grid.SetRow(SentNote, below ? 1 : 0);
        Grid.SetColumn(SentNote, below ? 0 : 2);
        Grid.SetColumnSpan(SentNote, below ? 3 : 1);
        SentActions.RowSpacing = below ? 6 : 0;
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
        // An empty second row would still add its gap.
        HeadGrid.RowSpacing = fits ? 0 : 12;
        HeadActions.HorizontalAlignment = HorizontalAlignment.Left;
    }
}

/// <summary>
/// The Audit page's width class, for its row template: on the phone the table drops its time column. Templates bind to
/// <see cref="Current"/>; the page sets it from the shell's layout.
/// </summary>
public sealed class AuditLayout : INotifyPropertyChanged
{
    private bool _isPhone;

    public static AuditLayout Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsPhone
    {
        get => _isPhone;
        set
        {
            if (_isPhone == value)
                return;
            _isPhone = value;
            // By name: x:Bind's static-rooted paths do not refresh on an empty property name.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPhone)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeVisibility)));
        }
    }

    public GridLength TimeWidth => new(_isPhone ? 0 : 72);

    public Visibility TimeVisibility => _isPhone ? Visibility.Collapsed : Visibility.Visible;
}
