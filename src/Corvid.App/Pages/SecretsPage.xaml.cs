using System.ComponentModel;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Models;
using Corvid.App.Core.Secrets;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Secrets area's page. Its view model comes from the composition the shell navigates with. The page arranges itself
/// for the width (the head's buttons go under the title when they do not fit; one column on a phone), puts focus in a
/// picker when it opens, and closes it on Esc or a click outside it.
/// </summary>
public sealed partial class SecretsPage : Page
{
    private const double PhoneWidth = 720;

    public SecretsPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Arrange();
        Loaded += OnLoaded;
        KeyDown += OnPageKeyDown;
        IsTabStop = true;
    }

    public SecretsViewModel ViewModel { get; private set; } = null!;

    /// <summary>The one way in today.</summary>
    public static IReadOnlyList<TextRun> HowSet { get; } = Spans.Parse(
        "`corvid-vault set NAME` asks for the value on its own terminal, or imports it from a `.env` file or `gh auth token`. "
        + "Corvid has no tool that writes a secret.");

    /// <summary>What plugs in later.</summary>
    public static IReadOnlyList<TextRun> HowLater { get; } = Spans.Parse(
        "Later: the Vault helper (U-6) in place of the terminal, a pasted credential caught before it is sent (V-005), "
        + "Vault's out-of-band confirm shown as pending (V-006), and OAuth sign-in (V-007).");

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel = ((AppComposition)e.Parameter).Secrets;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            if (ViewModel.IsPickerOpen)
                ViewModel.ClosePickerCommand.Execute(null);
        }

        Bindings.StopTracking();
        base.OnNavigatedFrom(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Arrange();
        Focus(FocusState.Programmatic);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A picker takes focus when it opens: the name field for Set, the card for Bind, so Tab and Esc work in it at once.
        if (e.PropertyName == nameof(SecretsViewModel.IsSetOpen) && ViewModel.IsSetOpen)
            // Low priority: after the layout pass that shows the picker, or the collapsed box refuses focus.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SetNameBox.Focus(FocusState.Programmatic);
                SetNameBox.SelectionStart = SetNameBox.Text.Length;
            });
        else if (e.PropertyName == nameof(SecretsViewModel.IsBindOpen) && ViewModel.IsBindOpen)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => PickerHost.Focus(FocusState.Programmatic));
        else if (e.PropertyName == nameof(SecretsViewModel.IsPickerOpen) && !ViewModel.IsPickerOpen)
            Focus(FocusState.Programmatic);
    }

    private void Arrange()
    {
        var width = ActualWidth;
        if (width <= 0)
            return;
        var phone = width < PhoneWidth;
        // The phone's top bar names the page already.
        PageTitle.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        Body.Padding = phone ? new Thickness(14, 14, 14, 18) : new Thickness(24, 18, 24, 18);
        HeadHost.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 12, 24, 12);

        var available = width - HeadHost.Padding.Left - HeadHost.Padding.Right;
        HeadTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        HeadButtons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var oneLine = HeadTitle.DesiredSize.Width + 12 + HeadButtons.DesiredSize.Width <= available;
        Grid.SetRow(HeadButtons, oneLine ? 0 : 1);
        Grid.SetColumn(HeadButtons, oneLine ? 1 : 0);
        HeadButtons.HorizontalAlignment = oneLine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        HeadButtons.Margin = oneLine ? new Thickness(0) : new Thickness(0, 10, 0, 0);
        Grid.SetColumnSpan(HeadTitle, oneLine ? 1 : 2);

        LeftColumn.Width = new GridLength(1.4, GridUnitType.Star);
        // One breakpoint across the A2 pages (OpsLayout): under it the right column goes under the left.
        var two = width >= OpsLayout.TwoColumnWidth;
        RightColumn.Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Columns.ColumnSpacing = two ? 18 : 0;
        Grid.SetColumn(RightStack, two ? 1 : 0);
        Grid.SetRow(RightStack, two ? 0 : 1);
        PickerCard.Width = Math.Min(480, Math.Max(0, width - 32));
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.IsPickerOpen)
        {
            ViewModel.ClosePickerCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Enter in the name field starts the set, as Open in a terminal does.</summary>
    private void OnSetNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (ViewModel.StartSetCommand.CanExecute(null))
            ViewModel.StartSetCommand.Execute(null);
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => ViewModel.ClosePickerCommand.Execute(null);
}
