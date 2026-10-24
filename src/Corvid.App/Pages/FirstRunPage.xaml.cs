using System.ComponentModel;
using Corvid.App.Core.Composition;
using Corvid.App.Core.FirstRun;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The first run's page (FirstRun.png). Its view model comes from the composition the shell navigates with. The page
/// shows the setup's steps column while the setup runs and the window is 860 px or wider, brings a step's panel into view
/// when the step changes, copies a fix's command to the clipboard, arranges the head and the columns for the width, and
/// closes the folder list on Esc.
/// </summary>
public sealed partial class FirstRunPage : Page
{
    private const double PhoneWidth = 720;
    private const double RailWidth = 860;

    public FirstRunPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Arrange();
        Loaded += (_, _) => Arrange();
        KeyDown += OnPageKeyDown;
    }

    public FirstRunViewModel ViewModel { get; private set; } = null!;

    /// <summary>A check's line below it, except under the section's last (the panel's border is there).</summary>
    public static Thickness Divider(bool isLast) => isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Detach();
        ViewModel = ((AppComposition)e.Parameter).FirstRun;
        ViewModel.ScrollRequested += OnScrollRequested;
        ViewModel.CopyRequested += OnCopyRequested;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Bindings.Update();
        Arrange();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Detach();
        base.OnNavigatedFrom(e);
    }

    private void Detach()
    {
        if (ViewModel is null)
            return;
        ViewModel.ScrollRequested -= OnScrollRequested;
        ViewModel.CopyRequested -= OnCopyRequested;
        ViewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FirstRunViewModel.IsSetup) or nameof(FirstRunViewModel.Heading) or nameof(FirstRunViewModel.NextLabel)
            or nameof(FirstRunViewModel.ShowHeadSkip))
            Arrange();
    }

    private void Arrange()
    {
        var width = ActualWidth;
        if (width <= 0 || ViewModel is null)
            return;
        var phone = width < PhoneWidth;
        StepsRail.Visibility = ViewModel.IsSetup && width >= RailWidth ? Visibility.Visible : Visibility.Collapsed;
        // Without the column the shell's rail toggle comes back, and Skip setup moves into the head.
        ViewModel.IsStepsColumnShown = width >= RailWidth;
        var content = width - (StepsRail.Visibility == Visibility.Visible ? StepsRail.Width : 0);
        Body.Padding = phone ? new Thickness(14, 14, 14, 18) : new Thickness(24, 18, 24, 18);
        HeadHost.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 12, 24, 12);

        var available = content - HeadHost.Padding.Left - HeadHost.Padding.Right;
        HeadTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        HeadButtons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var oneLine = HeadTitle.DesiredSize.Width + 12 + HeadButtons.DesiredSize.Width <= available;
        Grid.SetRow(HeadButtons, oneLine ? 0 : 1);
        Grid.SetColumn(HeadButtons, oneLine ? 1 : 0);
        HeadButtons.HorizontalAlignment = oneLine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        HeadButtons.Margin = oneLine ? new Thickness(0) : new Thickness(0, 12, 0, 0);
        Grid.SetColumnSpan(HeadTitle, oneLine ? 1 : 2);

        RightColumn.Width = phone ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Columns.ColumnSpacing = phone ? 0 : 18;
        Grid.SetColumn(RightStack, phone ? 0 : 1);
        Grid.SetRow(RightStack, phone ? 1 : 0);
    }

    private void OnScrollRequested(object? sender, string anchor)
    {
        FrameworkElement? target = anchor switch
        {
            "check" => CheckPanels,
            "models" => ModelsPanel,
            "workspace" => WorkspacePanel,
            "approvals" => ApprovalsPanel,
            "privacy" => PrivacyPanel,
            "import" => ImportPanel,
            _ => null,
        };
        if (target is null)
            return;
        // The step's panel at the top of the view, unless it is already wholly in view.
        var top = target.TransformToVisual(Body).TransformPoint(new Point(0, 0)).Y - 18;
        var bottom = top + target.ActualHeight + 18;
        var offset = BodyScroll.VerticalOffset;
        if (top >= offset && bottom <= offset + BodyScroll.ViewportHeight)
            return;
        BodyScroll.ChangeView(null, Math.Max(0, top), null, true);
    }

    private void OnCopyRequested(object? sender, CopyRequestEventArgs request)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(request.Text);
            Clipboard.SetContent(package);
            request.Copied = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The command stays on screen to copy by hand, and the notice says the copy failed; not worth a crash.
            System.Diagnostics.Trace.WriteLine($"Copy failed: {exception.Message}");
        }
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.IsChoosingWorkspace)
        {
            ViewModel.ClosePickerCommand.Execute(null);
            e.Handled = true;
        }
    }
}
