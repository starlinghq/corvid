using System.ComponentModel;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Corvid.App.Pages;

/// <summary>
/// The Models area's page (Models.png). Its view model comes from the composition the shell navigates with. The page
/// arranges itself for the width (the head's buttons go under the title when they do not fit beside it; the two columns
/// stack on a phone), moves ladder steps by drag or Alt+Left and Alt+Right, binds Alt+P to the generator's Switch… and
/// Esc to closing a picker.
/// </summary>
public sealed partial class ModelsPage : Page
{
    private const double PhoneWidth = 720;
    private const double DragThreshold = 4;

    private readonly PointerEventHandler _pressed;
    private readonly PointerEventHandler _released;
    private Button? _dragged;
    private Point _dragStart;
    private bool _dragging;

    public ModelsPage()
    {
        _pressed = OnStepPressed;
        _released = OnStepReleased;
        InitializeComponent();
        SizeChanged += (_, _) => Arrange();
        Loaded += (_, _) => Arrange();
        KeyDown += OnPageKeyDown;
        var switchGenerator = new KeyboardAccelerator { Key = VirtualKey.P, Modifiers = VirtualKeyModifiers.Menu };
        switchGenerator.Invoked += (_, args) =>
        {
            ViewModel?.SwitchGeneratorCommand.Execute(null);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(switchGenerator);
    }

    public ModelsViewModel ViewModel { get; private set; } = null!;

    /// <summary>"Switch the generator…", the Switch… button's name for a screen reader.</summary>
    public static string SwitchName(string slot) => $"Switch the {slot}…";

    /// <summary>"Cancel kev-4b", the catalogue action's name for a screen reader.</summary>
    public static string ActionName(string action, string model) => $"{action.TrimEnd('…')} {model}";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel = ((AppComposition)e.Parameter).Models;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= OnViewModelChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A picker takes focus when it opens, so Tab and Esc work in it at once.
        // Its rows are generated in the next layout pass, so focus waits for that.
        if (e.PropertyName == nameof(ModelsViewModel.IsPickerOpen) && ViewModel.IsPickerOpen)
            DispatcherQueue.TryEnqueue(() => PickerHost.Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// The head on one line when the title, the config line and the buttons fit (CSS flex-wrap with space-between), the
    /// buttons on their own line otherwise; two columns, or one on a phone.
    /// </summary>
    private void Arrange()
    {
        var width = ActualWidth;
        if (width <= 0)
            return;
        var phone = width < PhoneWidth;
        Body.Padding = phone ? new Thickness(14, 14, 14, 18) : new Thickness(24, 18, 24, 18);
        HeadHost.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 12, 24, 12);

        var available = width - HeadHost.Padding.Left - HeadHost.Padding.Right;
        HeadTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        HeadButtons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var oneLine = HeadTitle.DesiredSize.Width + 12 + HeadButtons.DesiredSize.Width <= available;
        Grid.SetRow(HeadButtons, oneLine ? 0 : 1);
        Grid.SetColumn(HeadButtons, oneLine ? 1 : 0);
        HeadButtons.HorizontalAlignment = oneLine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        HeadButtons.Margin = oneLine ? new Thickness(0) : new Thickness(0, 12, 0, 0);
        Grid.SetColumnSpan(HeadTitle, oneLine ? 1 : 2);

        LeftColumn.Width = new GridLength(1.4, GridUnitType.Star);
        RightColumn.Width = phone ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Columns.ColumnSpacing = phone ? 0 : 18;
        Grid.SetColumn(RightStack, phone ? 0 : 1);
        Grid.SetRow(RightStack, phone ? 1 : 0);
        PickerCard.Width = Math.Min(460, Math.Max(0, width - 32));
    }

    /// <summary>
    /// A table fills its panel, and scrolls sideways only when the panel is narrower than the mockup's columns: inside a
    /// horizontal scroller its content would otherwise be measured as wide as it likes.
    /// </summary>
    private void OnTableScrollSized(object sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer { Content: FrameworkElement table } && e.NewSize.Width > 0)
            table.Width = Math.Max(table.MinWidth, e.NewSize.Width);
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && (ViewModel.IsPickerOpen || ViewModel.IsEditingPlacement))
        {
            ViewModel.ClosePickerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => ViewModel.ClosePickerCommand.Execute(null);

    // ---- Ladder steps: drag to reorder, or Alt+Left and Alt+Right ----

    private void OnStepKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button { Tag: LadderStepViewModel step } || !IsDown(VirtualKey.Menu))
            return;
        var command = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => step.MoveEarlierCommand,
            VirtualKey.Right or VirtualKey.Down => step.MoveLaterCommand,
            _ => null,
        };
        if (command is null)
            return;
        e.Handled = true;
        if (!command.CanExecute(null))
            return;
        var slot = step.Slot;
        var target = e.Key is VirtualKey.Left or VirtualKey.Up ? step.Index - 1 : step.Index + 1;
        command.Execute(null);
        FocusStep(slot, target);
    }

    /// <summary>A button marks its pointer presses handled, so the drag listens for handled ones too.</summary>
    private void OnStepLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;
        // Loaded comes again when a row is re-entered; take the handlers off first so each is added once.
        button.RemoveHandler(PointerPressedEvent, _pressed);
        button.RemoveHandler(PointerReleasedEvent, _released);
        button.AddHandler(PointerPressedEvent, _pressed, handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, _released, handledEventsToo: true);
    }

    private void OnStepPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
            return;
        _dragged = button;
        _dragStart = e.GetCurrentPoint(this).Position;
        _dragging = false;
        button.CapturePointer(e.Pointer);
    }

    private void OnStepMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragged is null || !ReferenceEquals(sender, _dragged))
            return;
        var at = e.GetCurrentPoint(this).Position;
        var dx = at.X - _dragStart.X;
        var dy = at.Y - _dragStart.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < DragThreshold)
            return;
        _dragging = true;
        _dragged.Opacity = 0.7;
        _dragged.RenderTransform = new TranslateTransform { X = dx, Y = dy };
        Canvas.SetZIndex(_dragged, 10);
    }

    private void OnStepReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragged is not { Tag: LadderStepViewModel step } button || !ReferenceEquals(sender, button))
            return;
        var dragging = _dragging;
        var at = e.GetCurrentPoint(this).Position;
        EndDrag(e.Pointer);
        if (!dragging)
            return;
        e.Handled = true;
        var target = DropIndex(step, at);
        if (target != step.Index)
        {
            var slot = step.Slot;
            slot.MoveStep(step.Index, target);
            FocusStep(slot, target);
        }
    }

    private void OnStepCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _dragged))
            EndDrag(null);
    }

    private void EndDrag(Pointer? pointer)
    {
        if (_dragged is null)
            return;
        var button = _dragged;
        _dragged = null;
        _dragging = false;
        button.Opacity = 1;
        button.RenderTransform = null;
        if (pointer is not null)
            button.ReleasePointerCapture(pointer);
    }

    /// <summary>
    /// Where a dropped step goes: after every other step of its ladder that the pointer is past, in reading order (a later
    /// line, or the same line beyond the step's middle).
    /// </summary>
    private int DropIndex(LadderStepViewModel step, Point at)
    {
        var before = 0;
        foreach (var other in StepButtons(step.Slot))
        {
            if (other.Tag is not LadderStepViewModel candidate || candidate == step)
                continue;
            var bounds = other.TransformToVisual(this).TransformBounds(new Rect(0, 0, other.ActualWidth, other.ActualHeight));
            var pastLine = at.Y > bounds.Bottom + 3;
            var sameLine = at.Y >= bounds.Top - 3 && at.Y <= bounds.Bottom + 3;
            if (pastLine || sameLine && at.X > bounds.Left + bounds.Width / 2)
                before++;
        }

        return before;
    }

    private void FocusStep(SlotRowViewModel slot, int index) => DispatcherQueue.TryEnqueue(() =>
    {
        var button = StepButtons(slot).FirstOrDefault(candidate => candidate.Tag is LadderStepViewModel step && step.Index == index);
        button?.Focus(FocusState.Keyboard);
    });

    private List<Button> StepButtons(SlotRowViewModel slot)
    {
        var found = new List<Button>();
        Collect(this, slot, found);
        return found;
    }

    private static void Collect(DependencyObject parent, SlotRowViewModel slot, List<Button> found)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button { Tag: LadderStepViewModel step } button && step.Slot == slot)
                found.Add(button);
            else
                Collect(child, slot, found);
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
